using Microsoft.Extensions.Logging;
using Sms.Application.Common;
using Sms.Application.Interfaces.DAO;
using Sms.Modules.Tenancy.Contracts;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using System.Text.Json;

namespace Sms.Application.Services.Tenancy;

public interface IPlanUpgradeService
{
    Task<ApiResult<PlanUpgradeRequestResponse>> CreateForOwnerAsync(
        Guid tenantId, CreatePlanUpgradeRequest req, CancellationToken ct = default);
    Task<ApiResult<PlanUpgradeRequestResponse>> CreateForPlatformAsync(
        Guid tenantId, CreatePlanPaymentRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListForOwnerAsync(CancellationToken ct = default);
    Task<ApiResult<RazorpayOrderResponse>> CreateRazorpayOrderAsync(Guid requestId, CancellationToken ct = default);
    Task<ApiResult<PlanUpgradeRequestResponse>> ConfirmPaymentAsync(
        Guid requestId, ConfirmPlanUpgradePaymentRequest req, CancellationToken ct = default);
    Task HandleRazorpayWebhookAsync(string rawBody, string? signatureHeader, CancellationToken ct = default);
    PaymentGatewayStatusResponse GetGatewayStatus();

    Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListForPlatformAsync(string? status, CancellationToken ct = default);
    Task<ApiResult<PlanUpgradeRequestResponse>> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<PlanUpgradeRequestResponse>> RejectAsync(Guid id, RejectPlanUpgradeRequest req, CancellationToken ct = default);
}

public sealed class PlanUpgradeService(
    ITenantContext tenant,
    IAuthDao auth,
    ClientRepository clients,
    PlanRepository plans,
    InvoiceRepository invoices,
    SubscriptionRepository subscriptions,
    PlanUpgradeRequestRepository upgrades,
    AuditRepository audit,
    IRazorpayGateway razorpay,
    ClientBillingEmails billingEmails,
    ILogger<PlanUpgradeService> logger) : IPlanUpgradeService
{
    private static readonly HashSet<string> OpenStatuses =
    [
        PlanUpgradeStatuses.PendingPayment,
        PlanUpgradeStatuses.PendingOffline,
        PlanUpgradeStatuses.PaidPendingApproval,
    ];

    public async Task<ApiResult<PlanUpgradeRequestResponse>> CreateForOwnerAsync(
        Guid tenantId, CreatePlanUpgradeRequest req, CancellationToken ct = default)
    {
        if (tenant.IsPlatform)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("forbidden", "Platform operators use Catre change-plan."), 403);
        if (tenant.UserId is not { } uid)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var mode = (req.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not (PlanUpgradeModes.Online or PlanUpgradeModes.Offline))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "mode must be online or offline"), 422);

        var owned = await EnsureOwnsTenantAsync(tenantId, uid, ct);
        if (owned is not null) return owned;

        var client = await clients.GetAsync(tenantId, ct);
        if (client is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "school not found"), 404);

        var toPlan = await plans.GetAsync(req.PlanId, ct);
        if (toPlan is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "plan not found"), 404);
        if (!string.Equals(toPlan.Visibility, "published", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toPlan.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "plan is not published"), 422);

        /* Allow paying for the chosen plan while school is still on trial (create-school flow).
           Otherwise require a true tier upgrade. */
        var isTrial = string.Equals(client.Status, "trial", StringComparison.OrdinalIgnoreCase);
        var samePlan = client.PlanId == toPlan.Id;
        if (samePlan && !isTrial)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "school is already active on this plan"), 422);

        if (!samePlan && client.PlanId is { } fromId)
        {
            var fromPlan = await plans.GetAsync(fromId, ct);
            var fromTier = fromPlan?.Tier ?? client.Tier;
            if (!isTrial && !PlanTier.IsUpgrade(fromTier, toPlan.Tier))
                return ApiResult<PlanUpgradeRequestResponse>.Fail(
                    new Error("invalid_request", "target plan must be a higher tier"), 422);
        }
        else if (!samePlan && client.PlanId is null && PlanTier.Rank(toPlan.Tier) <= 0 && !isTrial)
        {
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "target plan tier is not upgradable"), 422);
        }

        var existing = await upgrades.ListByTenantsAsync([tenantId], ct);
        if (existing.Any(r => OpenStatuses.Contains(r.Status)))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "an upgrade request is already in progress for this school"), 409);

        var seats = CatreMappers.BillableSeats(toPlan, client.StudentsCount, client.LimitsStudents ?? 0);
        var amount = CatreMappers.ComputeMonthlyAmount(toPlan, client.StudentsCount, seats);
        if (amount <= 0)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "plan price must be greater than zero"), 422);

        var status = mode == PlanUpgradeModes.Online
            ? PlanUpgradeStatuses.PendingPayment
            : PlanUpgradeStatuses.PendingOffline;

        var row = await upgrades.CreateAsync(
            tenantId, client.PlanId, toPlan.Id, amount, "INR", mode, status, uid, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("internal_error", "could not create request"), 500);

        var invoice = await invoices.CreateAsync(new CreateInvoiceRequest(
            tenantId, client.Name, toPlan.Name, amount, DateTime.UtcNow.AddDays(14)), ct);
        if (invoice is not null)
            row = await upgrades.AttachInvoiceAsync(row.Id, invoice.Id, ct) ?? row;

        return ApiResult<PlanUpgradeRequestResponse>.Ok(row, 201);
    }

    public async Task<ApiResult<PlanUpgradeRequestResponse>> CreateForPlatformAsync(
        Guid tenantId, CreatePlanPaymentRequest req, CancellationToken ct = default)
    {
        if (!tenant.IsPlatform)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("forbidden", "platform only"), 403);
        if (tenant.UserId is not { } uid)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var mode = (req.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not (PlanUpgradeModes.Online or PlanUpgradeModes.Offline))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "mode must be online or offline"), 422);

        var client = await clients.GetAsync(tenantId, ct);
        if (client is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "school not found"), 404);

        var toPlan = await plans.GetAsync(req.PlanId, ct);
        if (toPlan is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "plan not found"), 404);

        var isTrial = string.Equals(client.Status, "trial", StringComparison.OrdinalIgnoreCase);
        var samePlan = client.PlanId == toPlan.Id;
        if (samePlan && !isTrial)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "school is already active on this plan"), 422);

        if (!samePlan && client.PlanId is { } fromId)
        {
            var fromPlan = await plans.GetAsync(fromId, ct);
            var fromTier = fromPlan?.Tier ?? client.Tier;
            if (!isTrial && !PlanTier.IsUpgrade(fromTier, toPlan.Tier))
                return ApiResult<PlanUpgradeRequestResponse>.Fail(
                    new Error("invalid_request", "target plan must be a higher tier (or collect payment while school is still on trial)"), 422);
        }

        var existing = await upgrades.ListByTenantsAsync([tenantId], ct);
        if (existing.Any(r => OpenStatuses.Contains(r.Status)))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "a plan payment request is already in progress for this school"), 409);

        var seats = CatreMappers.BillableSeats(toPlan, client.StudentsCount, client.LimitsStudents ?? 0);
        var amount = CatreMappers.ComputeMonthlyAmount(toPlan, client.StudentsCount, seats);
        if (amount <= 0)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "plan price must be greater than zero"), 422);

        var status = mode == PlanUpgradeModes.Online
            ? PlanUpgradeStatuses.PendingPayment
            : PlanUpgradeStatuses.PendingOffline;

        var row = await upgrades.CreateAsync(
            tenantId, client.PlanId, toPlan.Id, amount, "INR", mode, status, uid, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("internal_error", "could not create request"), 500);

        var invoice = await invoices.CreateAsync(new CreateInvoiceRequest(
            tenantId, client.Name, toPlan.Name, amount, DateTime.UtcNow.AddDays(14)), ct);
        if (invoice is not null)
            row = await upgrades.AttachInvoiceAsync(row.Id, invoice.Id, ct) ?? row;

        return ApiResult<PlanUpgradeRequestResponse>.Ok(row, 201);
    }

    public async Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListForOwnerAsync(CancellationToken ct = default)
    {
        var ids = await OwnedTenantIdsAsync(ct);
        if (ids.Count == 0) return [];
        return await upgrades.ListByTenantsAsync(ids, ct);
    }

    public async Task<ApiResult<RazorpayOrderResponse>> CreateRazorpayOrderAsync(Guid requestId, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<RazorpayOrderResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var row = await upgrades.GetAsync(requestId, ct);
        if (row is null)
            return ApiResult<RazorpayOrderResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (!tenant.IsPlatform)
        {
            var owned = await EnsureOwnsTenantAsync(row.TenantId, uid, ct);
            if (owned is not null)
                return ApiResult<RazorpayOrderResponse>.Fail(owned.Error!, owned.StatusCode);
        }

        if (!string.Equals(row.Mode, PlanUpgradeModes.Online, StringComparison.OrdinalIgnoreCase))
            return ApiResult<RazorpayOrderResponse>.Fail(
                new Error("invalid_request", "razorpay only for online upgrade requests"), 422);
        if (row.Status is not PlanUpgradeStatuses.PendingPayment)
            return ApiResult<RazorpayOrderResponse>.Fail(
                new Error("conflict", "request is not awaiting payment"), 409);

        /* Never treat unpaid Razorpay as paid — keys missing means checkout cannot run. */
        if (!razorpay.IsConfigured)
            return ApiResult<RazorpayOrderResponse>.Fail(
                new Error("payment_not_configured", "Razorpay keys are not configured on the server"), 503);

        if (!string.IsNullOrEmpty(row.RazorpayOrderId))
        {
            return ApiResult<RazorpayOrderResponse>.Ok(new RazorpayOrderResponse(
                razorpay.KeyId, row.RazorpayOrderId, (long)(row.Amount * 100), row.Currency, row.TenantName ?? "SchoolMate", row.Id));
        }

        var amountPaise = (long)Math.Round(row.Amount * 100m, MidpointRounding.AwayFromZero);
        try
        {
            var order = await razorpay.CreateOrderAsync(amountPaise, row.Currency, row.Id.ToString("N"), ct);
            row = await upgrades.AttachRazorpayAsync(row.Id, order.OrderId, null, null, ct) ?? row;
            return ApiResult<RazorpayOrderResponse>.Ok(new RazorpayOrderResponse(
                razorpay.KeyId, order.OrderId, order.AmountPaise, order.Currency, row.TenantName ?? "SchoolMate", row.Id));
        }
        catch (Exception)
        {
            return ApiResult<RazorpayOrderResponse>.Fail(
                new Error("payment_error", "could not create Razorpay order"), 502);
        }
    }

    public PaymentGatewayStatusResponse GetGatewayStatus() =>
        new(razorpay.IsConfigured);

    public async Task<ApiResult<PlanUpgradeRequestResponse>> ConfirmPaymentAsync(
        Guid requestId, ConfirmPlanUpgradePaymentRequest req, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        var row = await upgrades.GetAsync(requestId, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (!tenant.IsPlatform)
        {
            var owned = await EnsureOwnsTenantAsync(row.TenantId, uid, ct);
            if (owned is not null) return owned;
        }

        if (row.Status == PlanUpgradeStatuses.PaidPendingApproval)
            return ApiResult<PlanUpgradeRequestResponse>.Ok(row);

        if (row.Status != PlanUpgradeStatuses.PendingPayment)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "request is not awaiting payment"), 409);

        if (!razorpay.VerifyPaymentSignature(req.RazorpayOrderId, req.RazorpayPaymentId, req.RazorpaySignature))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "invalid payment signature"), 422);

        if (!string.IsNullOrEmpty(row.RazorpayOrderId)
            && !string.Equals(row.RazorpayOrderId, req.RazorpayOrderId, StringComparison.Ordinal))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("invalid_request", "order does not match this request"), 422);

        row = await upgrades.AttachRazorpayAsync(
            row.Id, req.RazorpayOrderId, req.RazorpayPaymentId, PlanUpgradeStatuses.PaidPendingApproval, ct);
        return row is null
            ? ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("internal_error", "update failed"), 500)
            : ApiResult<PlanUpgradeRequestResponse>.Ok(row);
    }

    public async Task HandleRazorpayWebhookAsync(string rawBody, string? signatureHeader, CancellationToken ct = default)
    {
        if (!razorpay.VerifyWebhookSignature(rawBody, signatureHeader ?? ""))
            return;

        using var doc = JsonDocument.Parse(rawBody);
        var eventName = doc.RootElement.TryGetProperty("event", out var ev) ? ev.GetString() : null;
        if (eventName is not ("payment.captured" or "order.paid"))
            return;

        if (!doc.RootElement.TryGetProperty("payload", out var payload))
            return;

        string? orderId = null;
        string? paymentId = null;
        if (payload.TryGetProperty("payment", out var payment)
            && payment.TryGetProperty("entity", out var payEnt))
        {
            paymentId = payEnt.TryGetProperty("id", out var pid) ? pid.GetString() : null;
            orderId = payEnt.TryGetProperty("order_id", out var oid) ? oid.GetString() : null;
        }
        if (orderId is null && payload.TryGetProperty("order", out var order)
            && order.TryGetProperty("entity", out var ordEnt))
        {
            orderId = ordEnt.TryGetProperty("id", out var oid) ? oid.GetString() : null;
        }
        if (string.IsNullOrEmpty(orderId))
            return;

        var row = await upgrades.GetByOrderAsync(orderId, ct);
        if (row is null || row.Status != PlanUpgradeStatuses.PendingPayment)
            return;

        await upgrades.AttachRazorpayAsync(row.Id, orderId, paymentId, PlanUpgradeStatuses.PaidPendingApproval, ct);
    }

    public Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListForPlatformAsync(
        string? status, CancellationToken ct = default) =>
        upgrades.ListAsync(string.IsNullOrWhiteSpace(status) ? null : status, ct);

    public async Task<ApiResult<PlanUpgradeRequestResponse>> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        var row = await upgrades.GetAsync(id, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (row.Status is not (PlanUpgradeStatuses.PaidPendingApproval or PlanUpgradeStatuses.PendingOffline))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "only paid online or pending offline requests can be approved"), 409);

        /* Claim first: of several concurrent approvals of this request exactly one flips it to
           approved, and only that one changes the school's plan, invoice and status, writes the
           audit entry and sends the email. */
        var awaitingStatus = row.Status;
        var reviewer = tenant.UserId;
        var approved = await upgrades.TryApproveAsync(id, reviewer, ct);
        if (approved is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "only paid online or pending offline requests can be approved"), 409);

        ClientRow client;
        PlanRow? plan;
        var seats = 0;
        bool activatesTrial;
        try
        {
            var changed = await clients.ChangePlanAsync(approved.TenantId, approved.ToPlanId, ct);
            if (changed is null)
            {
                await upgrades.ReleaseApprovalAsync(id, awaitingStatus, CancellationToken.None);
                return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "school not found"), 404);
            }
            client = changed;

            plan = await plans.GetAsync(approved.ToPlanId, ct);
            if (plan is not null)
            {
                seats = CatreMappers.BillableSeats(plan, client.StudentsCount, client.LimitsStudents ?? 0);
                var amount = CatreMappers.ComputeMonthlyAmount(plan, client.StudentsCount, seats);
                if (client.Mrr != amount)
                    client = await clients.SetMrrAsync(approved.TenantId, amount, ct) ?? client;
                await subscriptions.SetPlanAsync(approved.TenantId, approved.ToPlanId, seats, ct);
            }

            if (approved.InvoiceId is { } invId)
            {
                var inv = await invoices.GetAsync(invId, ct);
                if (inv is not null && !string.Equals(inv.Status, "paid", StringComparison.OrdinalIgnoreCase))
                    await invoices.MarkPaidAsync(invId, ct);
            }

            /* Activation payment: move trial schools to active when payment is approved. */
            activatesTrial = string.Equals(client.Status, "trial", StringComparison.OrdinalIgnoreCase);
            if (activatesTrial)
                client = await clients.SetStatusAsync(approved.TenantId, "active", ct) ?? client;
        }
        catch
        {
            /* Hand the request back so the approval can simply be retried; every step above is
               safe to repeat (same plan, same seats, paid stays paid, active stays active). */
            await upgrades.ReleaseApprovalAsync(id, awaitingStatus, CancellationToken.None);
            throw;
        }
        row = approved;

        await audit.InsertAsync(
            reviewer, null, "platform",
            $"Approved plan payment {row.FromPlanName ?? "—"} → {row.ToPlanName}",
            row.TenantName, "plan", row.TenantId, ct);

        /* The approval is committed; a failure to build or queue the email must not turn it into
           an error the admin can no longer retry (a second approve is a 409). */
        try
        {
            await QueueApprovalEmailAsync(row, client, plan, seats, activatesTrial, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Plan payment {RequestId} for tenant {TenantId} was approved, but its email could not be queued.",
                row.Id, row.TenantId);
        }

        return ApiResult<PlanUpgradeRequestResponse>.Ok(row);
    }

    /// Emails the school contact about an approved plan payment, reusing the existing billing
    /// emails: the activation invoice (as POST /clients/{id}/status sends) when this approval made a
    /// trial school active, otherwise the now-paid invoice as the payment receipt. Queued only
    /// after every state change above has succeeded.
    private async Task QueueApprovalEmailAsync(
        PlanUpgradeRequestResponse row, ClientRow client, PlanRow? plan, int seats, bool activatedTrial, CancellationToken ct)
    {
        var invoice = row.InvoiceId is { } invoiceId ? await invoices.GetAsync(invoiceId, ct) : null;
        if (invoice is null)
        {
            logger.LogWarning("Plan payment {RequestId} for tenant {TenantId} was approved without an invoice; no email sent.",
                row.Id, row.TenantId);
            return;
        }

        var queued = activatedTrial && plan is not null
            ? await billingEmails.QueueActivationInvoiceAsync(client, invoice, plan, seats, ct)
            : await billingEmails.QueueInvoiceAsync(client, invoice, plan, ct);
        if (!queued)
            logger.LogWarning("Plan payment {RequestId} for tenant {TenantId} was approved, but the school has no contact email; no email sent.",
                row.Id, row.TenantId);
    }

    public async Task<ApiResult<PlanUpgradeRequestResponse>> RejectAsync(
        Guid id, RejectPlanUpgradeRequest req, CancellationToken ct = default)
    {
        var row = await upgrades.GetAsync(id, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (!OpenStatuses.Contains(row.Status))
            return ApiResult<PlanUpgradeRequestResponse>.Fail(
                new Error("conflict", "request is already closed"), 409);

        row = await upgrades.SetStatusAsync(id, PlanUpgradeStatuses.Rejected, tenant.UserId, req.Notes, ct);
        if (row is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("internal_error", "update failed"), 500);

        await audit.InsertAsync(
            tenant.UserId, null, "platform",
            $"Rejected plan upgrade to {row.ToPlanName}",
            row.TenantName, "plan", row.TenantId, ct);

        return ApiResult<PlanUpgradeRequestResponse>.Ok(row);
    }

    private async Task<ApiResult<PlanUpgradeRequestResponse>?> EnsureOwnsTenantAsync(
        Guid tenantId, Guid uid, CancellationToken ct)
    {
        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null)
            return ApiResult<PlanUpgradeRequestResponse>.Fail(new Error("unauthorized", "unauthorized"), 401);

        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var target = await auth.GetByEmailAndTenantAsync(me.Email, tenantId, ct);
            if (target is null)
                return ApiResult<PlanUpgradeRequestResponse>.Fail(
                    new Error("forbidden", "You do not own that school."), 403);
            return null;
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }

    private async Task<IReadOnlyList<Guid>> OwnedTenantIdsAsync(CancellationToken ct)
    {
        if (tenant.IsPlatform)
            return (await clients.ListAsync(null, null, null, ct)).Select(c => c.Id).ToList();
        if (tenant.UserId is not { } uid) return [];
        var me = await auth.GetByIdAsync(uid, ct);
        if (me?.Email is null) return [];
        tenant.Set(null, uid, isPlatform: true);
        try
        {
            var peers = await auth.ListByEmailAsync(me.Email, ct);
            return peers
                .Where(u => u.TenantId is not null && !u.IsPlatform)
                .Select(u => u.TenantId!.Value)
                .Distinct()
                .ToList();
        }
        finally
        {
            tenant.Set(me.TenantId, uid, isPlatform: false);
        }
    }
}
