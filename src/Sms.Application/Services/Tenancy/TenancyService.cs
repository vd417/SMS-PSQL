using Npgsql;
using Sms.Application.Common;
using Sms.Application.DTOs.Auth;
using Sms.Application.Interfaces.DAO;
using Sms.Application.Services.Auth;
using Sms.Modules.Tenancy.Contracts;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using System.Globalization;
using System.Text;

namespace Sms.Application.Services.Tenancy;

public interface ITenancyService
{
    Task<IReadOnlyList<ClientResponse>> ListClientsAsync(string? status, string? tier, string? q, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> GetClientAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> CreateClientAsync(CreateClientRequest req, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> UpdateClientProfileAsync(Guid id, UpdateSchoolProfileRequest req, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> SetClientStatusAsync(Guid id, SetStatusRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteClientAsync(Guid id, DeleteClientRequest req, CancellationToken ct = default);
    Task<ApiResult<ClientResponse>> ChangeClientPlanAsync(Guid id, ChangePlanRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<PlanResponse>> ListPlansAsync(string? visibility, string? audience, CancellationToken ct = default);
    Task<ApiResult<PlanResponse>> GetPlanAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<PlanResponse>> CreatePlanAsync(PlanUpsertRequest req, CancellationToken ct = default);
    Task<ApiResult<PlanResponse>> UpdatePlanAsync(Guid id, PlanUpsertRequest req, CancellationToken ct = default);
    Task<ApiResult<PlanResponse>> PublishPlanAsync(Guid id, PublishPlanRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<InvoiceResponse>> ListInvoicesAsync(string? status, Guid? tenantId, CancellationToken ct = default);
    Task<ApiResult<InvoiceResponse>> GetInvoiceAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<(byte[] Pdf, string FileName)>> GetInvoicePdfAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult> SendInvoiceEmailAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<InvoiceResponse>> MarkInvoicePaidAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<InvoiceResponse>> RefundInvoiceAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(string? status, Guid? tenantId, CancellationToken ct = default);
    Task<ApiResult<SubscriptionResponse>> GetSubscriptionAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<SubscriptionResponse>> CreateSubscriptionAsync(CreateSubscriptionRequest req, CancellationToken ct = default);

    Task<DashboardOverview> GetDashboardOverviewAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OnboardingItemResponse>> ListOnboardingAsync(string? stage, CancellationToken ct = default);
    Task<ApiResult<OnboardingItemResponse>> CreateOnboardingAsync(CreateOnboardingRequest req, CancellationToken ct = default);
    Task<ApiResult<OnboardingItemResponse>> AdvanceOnboardingAsync(Guid id, AdvanceRequest req, CancellationToken ct = default);
    Task<ApiResult<OnboardingItemResponse>> UpdateOnboardingChecklistAsync(Guid id, ChecklistRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<TicketResponse>> ListTicketsAsync(string? status, string? q, CancellationToken ct = default);
    Task<ApiResult<TicketDetailResponse>> GetTicketAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<TicketDetailResponse>> CreateTicketAsync(CreateTicketRequest req, CancellationToken ct = default);
    Task<ApiResult<TicketResponse>> UpdateTicketAsync(Guid id, UpdateTicketRequest req, CancellationToken ct = default);
    Task<ApiResult<TicketDetailResponse>> AddTicketMessageAsync(Guid id, AddMessageRequest req, string actorId, CancellationToken ct = default);

    Task<IReadOnlyList<TeamMemberResponse>> ListTeamAsync(CancellationToken ct = default);
    Task<ApiResult<TeamMemberResponse>> InviteTeamMemberAsync(InviteTeamRequest req, CancellationToken ct = default);
    Task<ApiResult<TeamMemberResponse>> UpdateTeamMemberAsync(Guid id, UpdateTeamRequest req, CancellationToken ct = default);
    Task<ApiResult<TeamDocumentMeta>> AddTeamDocumentAsync(Guid memberId, TeamDocumentInput req, CancellationToken ct = default);
    Task<ApiResult<TeamDocumentDetail>> GetTeamDocumentAsync(Guid memberId, Guid docId, CancellationToken ct = default);
    Task<ApiResult> DeleteTeamDocumentAsync(Guid memberId, Guid docId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string? kind, Guid? actorId, Guid? tenantId, CancellationToken ct = default);

    Task<RevenueReport> GetRevenueReportAsync(CancellationToken ct = default);
    Task<string> ExportClientsCsvAsync(CancellationToken ct = default);
}

public sealed class TenancyService(
    ClientRepository clients,
    PlanRepository plans,
    InvoiceRepository invoices,
    SubscriptionRepository subscriptions,
    DashboardRepository dashboard,
    OnboardingRepository onboarding,
    TicketRepository tickets,
    TeamRepository team,
    AuditRepository audit,
    ReportRepository reports,
    IUserProvisioningDao users,
    UserProvisioningRepository platformUsers,
    IAuthService auth,
    IEmailQueue emailQueue,
    IInvoicePdfGenerator invoicePdf) : ITenancyService
{
    private static readonly HashSet<string> CatreRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "owner", "admin", "support", "sales", "finance", "analyst",
    };

    public async Task<IReadOnlyList<ClientResponse>> ListClientsAsync(string? status, string? tier, string? q, CancellationToken ct = default)
    {
        var rows = await clients.ListAsync(status, tier, q);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<ApiResult<ClientResponse>> GetClientAsync(Guid id, CancellationToken ct = default)
    {
        var row = await clients.GetAsync(id);
        return row is null
            ? ApiResult<ClientResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<ClientResponse>.Ok(row.ToResponse());
    }

    public async Task<ApiResult<ClientResponse>> CreateClientAsync(CreateClientRequest req, CancellationToken ct = default)
    {
        var slug = await clients.AllocateUniqueSlugAsync(req.Slug, ct);
        var create = req with { Slug = slug };
        ClientRow? row;
        try
        {
            row = await clients.CreateAsync(create, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ApiResult<ClientResponse>.Fail(
                new Error("slug_taken", "A school with this name/slug already exists. Try a different school name."),
                409);
        }

        if (row is not null && (create.AdminEmail is not null || create.AdminPhone is not null))
        {
            await users.CreateUserAsync(row.Id, create.AdminEmail, create.AdminPhone, false,
                [Policies.SchoolOwner], ct);
            /* Welcome onboard email/SMS with school name + password-setup OTP. */
            var inviteId = create.AdminEmail ?? create.AdminPhone;
            if (!string.IsNullOrWhiteSpace(inviteId))
            {
                try { await auth.SendInviteSetupAsync(inviteId!, row.Name, "Owner", ct: ct); }
                catch { /* invite is best-effort — school already created */ }
            }
        }
        if (row is not null)
            await onboarding.CreateAsync(new CreateOnboardingRequest(
                row.Name, row.Slug, row.Csm, row.Mrr, "trial",
                create.AdminName, create.AdminEmail, create.AdminPhone, create.Address, row.Id));
        return ApiResult<ClientResponse>.Ok(row!.ToResponse(), 201);
    }

    public async Task<ApiResult<ClientResponse>> UpdateClientProfileAsync(
        Guid id, UpdateSchoolProfileRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name)
            && string.IsNullOrWhiteSpace(req.Slug)
            && string.IsNullOrWhiteSpace(req.Country)
            && string.IsNullOrWhiteSpace(req.Address)
            && string.IsNullOrWhiteSpace(req.ContactName)
            && string.IsNullOrWhiteSpace(req.ContactEmail)
            && string.IsNullOrWhiteSpace(req.ContactPhone)
            && !req.SetLogo
            && !req.SetImage
            && !req.SetGeofence)
        {
            return ApiResult<ClientResponse>.Fail(new Error("invalid_request", "No profile fields to update."), 422);
        }

        string? slug = null;
        if (!string.IsNullOrWhiteSpace(req.Slug))
        {
            slug = NormalizeSchoolSlug(req.Slug);
            if (slug is null)
                return ApiResult<ClientResponse>.Fail(
                    new Error("invalid_request", "school id must be 3–40 characters: letters, numbers, hyphens"), 422);
            if (await clients.SlugTakenByOtherAsync(id, slug, ct))
                return ApiResult<ClientResponse>.Fail(
                    new Error("slug_taken", "That school id is already in use. Choose another."), 409);
        }

        var patch = req with
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(),
            Slug = slug,
            Country = string.IsNullOrWhiteSpace(req.Country) ? null : req.Country.Trim(),
            Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim(),
            ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim(),
            ContactEmail = string.IsNullOrWhiteSpace(req.ContactEmail) ? null : req.ContactEmail.Trim(),
            ContactPhone = string.IsNullOrWhiteSpace(req.ContactPhone) ? null : req.ContactPhone.Trim(),
        };

        try
        {
            var row = await clients.UpdateProfileAsync(id, patch, ct);
            return row is null
                ? ApiResult<ClientResponse>.Fail(new Error("not_found", "resource not found"), 404)
                : ApiResult<ClientResponse>.Ok(row.ToResponse());
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ApiResult<ClientResponse>.Fail(
                new Error("slug_taken", "That school id is already in use. Choose another."), 409);
        }
    }

    /// <summary>Lowercase slug; null when invalid.</summary>
    private static string? NormalizeSchoolSlug(string raw)
    {
        var slug = raw.Trim().ToLowerInvariant();
        if (slug.Length is < 3 or > 40) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, @"^[a-z0-9]+(?:-[a-z0-9]+)*$")) return null;
        return slug;
    }

    public async Task<ApiResult<ClientResponse>> SetClientStatusAsync(Guid id, SetStatusRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Status))
            return ApiResult<ClientResponse>.Fail(new Error("invalid_request", "status is required"), 422);

        var status = req.Status.Trim().ToLowerInvariant();
        if (status is not ("trial" or "active" or "past_due" or "hold" or "deactivated" or "suspended" or "cancelled"))
            return ApiResult<ClientResponse>.Fail(
                new Error("invalid_request", "status must be trial, active, past_due, hold, deactivated, suspended, or cancelled"), 422);

        var row = await clients.SetStatusAsync(id, status);
        if (row is null)
            return ApiResult<ClientResponse>.Fail(new Error("not_found", "resource not found"), 404);

        // Keep onboarding kanban in sync with tenant lifecycle.
        var onboardStage = status switch
        {
            "active" => "active",
            "trial" => "trial",
            "cancelled" or "deactivated" => "lead",
            _ => null,
        };
        if (onboardStage is not null)
            await onboarding.AdvanceByTenantAsync(id, onboardStage, ct);

        if (status == "active")
            await EnsureBillingOnActivateAsync(row, ct);

        return ApiResult<ClientResponse>.Ok(row.ToResponse());
    }

    public async Task<ApiResult> DeleteClientAsync(Guid id, DeleteClientRequest req, CancellationToken ct = default)
    {
        if (!string.Equals(req.Confirm?.Trim(), "DELETE", StringComparison.Ordinal))
            return ApiResult.Fail(new Error("invalid_request", "confirm must be DELETE"), 422);

        var result = await clients.DeleteEmptyAsync(id, ct);
        if (result is null)
            return ApiResult.Fail(new Error("internal_error", "delete failed"), 500);

        if (!result.Ok)
        {
            if (string.Equals(result.Code, "not_found", StringComparison.OrdinalIgnoreCase))
                return ApiResult.Fail(new Error("not_found", "school not found"), 404);
            if (string.Equals(result.Code, "has_people", StringComparison.OrdinalIgnoreCase))
                return ApiResult.Fail(new Error("conflict",
                    $"Cannot delete: school has {result.Students} student(s) and {result.Teachers + result.Staff} staff/teacher(s). Remove them first."), 409);
            return ApiResult.Fail(new Error("conflict", "cannot delete school"), 409);
        }

        return ApiResult.NoContent();
    }

    /// <summary>
    /// On activate/reinstate: ensure an active subscription, open invoice, and billing email.
    /// Idempotent — skips artifacts that already exist for the tenant.
    /// </summary>
    private async Task EnsureBillingOnActivateAsync(ClientRow client, CancellationToken ct)
    {
        if (client.PlanId is null)
            return;
        var planId = client.PlanId.Value;

        var plan = await plans.GetAsync(planId, ct);
        if (plan is null)
            return;

        var seats = CatreMappers.BillableSeats(plan, client.StudentsCount, client.LimitsStudents ?? 0);
        var amount = CatreMappers.ComputeMonthlyAmount(plan, client.StudentsCount, seats);

        if (client.Mrr != amount)
        {
            var updated = await clients.SetMrrAsync(client.Id, amount, ct);
            if (updated is not null)
                client = updated;
        }

        var activeSubs = await subscriptions.ListAsync(client.Id, "active", ct);
        if (activeSubs.Count == 0)
        {
            await subscriptions.CreateAsync(
                new CreateSubscriptionRequest(client.Id, planId, seats), ct);
        }

        InvoiceResponse? invoice = null;
        var createdInvoice = false;
        var openInvoices = await invoices.ListAsync("open", client.Id, ct);
        if (openInvoices.Count == 0)
        {
            invoice = await invoices.CreateAsync(new CreateInvoiceRequest(
                client.Id,
                client.Name,
                client.PlanName ?? plan.Name,
                amount,
                DateTime.UtcNow.Date.AddDays(14)), ct);
            createdInvoice = invoice is not null;
        }
        else if (openInvoices[0].Amount != amount)
        {
            // Correct zero/stale open invoice when plan is per-student.
            invoice = await invoices.SetAmountAsync(openInvoices[0].Id, amount, ct) ?? openInvoices[0];
        }

        if (createdInvoice && invoice is not null && !string.IsNullOrWhiteSpace(client.ContactEmail))
        {
            var amountText = invoice.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var due = invoice.Due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var billableNote = string.Equals(plan.Pricing, "per_student", StringComparison.OrdinalIgnoreCase)
                ? $" ({seats} students × ₹{(plan.PerStudent ?? 0).ToString("0.##", CultureInfo.InvariantCulture)})"
                : "";
            var subs = await subscriptions.ListAsync(client.Id, "active", ct);
            var pdf = invoicePdf.Generate(InvoicePdfGenerator.From(invoice, client, plan, subs.FirstOrDefault()));
            var fileName = $"Catre-Invoice-{invoice.Id:N}.pdf";
            var body =
                $"Hello{(string.IsNullOrWhiteSpace(client.ContactName) ? "" : " " + client.ContactName)},\n\n" +
                $"{client.Name} is now an active Catre client on the {client.PlanName ?? plan.Name} plan.\n\n" +
                $"Invoice amount: ₹{amountText}{billableNote}\n" +
                $"Due date: {due}\n" +
                $"Status: {invoice.Status}\n\n" +
                "Please find the full invoice PDF attached (plan, students, usage & charges).\n\n" +
                "— Catre Technology";
            emailQueue.Enqueue(new EmailMessage(
                client.ContactEmail.Trim(),
                $"Invoice for {client.Name} — ₹{amountText}",
                body,
                pdf,
                fileName,
                "application/pdf"));
        }
    }

    public async Task<ApiResult<ClientResponse>> ChangeClientPlanAsync(Guid id, ChangePlanRequest req, CancellationToken ct = default)
    {
        var row = await clients.ChangePlanAsync(id, req.PlanId);
        if (row is null)
            return ApiResult<ClientResponse>.Fail(new Error("not_found", "resource not found"), 404);

        var plan = await plans.GetAsync(req.PlanId, ct);
        if (plan is not null)
        {
            var amount = CatreMappers.ComputeMonthlyAmount(plan, row.StudentsCount, row.LimitsStudents ?? 0);
            if (row.Mrr != amount)
                row = await clients.SetMrrAsync(id, amount, ct) ?? row;
        }

        return ApiResult<ClientResponse>.Ok(row.ToResponse());
    }

    public async Task<IReadOnlyList<PlanResponse>> ListPlansAsync(string? visibility, string? audience, CancellationToken ct = default)
    {
        var rows = await plans.ListAsync(visibility, audience);
        return rows.Select(r => r.ToResponse()).ToList();
    }

    public async Task<ApiResult<PlanResponse>> GetPlanAsync(Guid id, CancellationToken ct = default)
    {
        var row = await plans.GetAsync(id);
        return row is null
            ? ApiResult<PlanResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<PlanResponse>.Ok(row.ToResponse());
    }

    public async Task<ApiResult<PlanResponse>> CreatePlanAsync(PlanUpsertRequest req, CancellationToken ct = default)
    {
        var row = await plans.UpsertAsync(req);
        return ApiResult<PlanResponse>.Ok(row!.ToResponse(), 201);
    }

    public async Task<ApiResult<PlanResponse>> UpdatePlanAsync(Guid id, PlanUpsertRequest req, CancellationToken ct = default)
    {
        if (await plans.GetAsync(id) is null)
            return ApiResult<PlanResponse>.Fail(new Error("not_found", "resource not found"), 404);
        var row = await plans.UpsertAsync(req with { Id = id });
        return ApiResult<PlanResponse>.Ok(row!.ToResponse());
    }

    public async Task<ApiResult<PlanResponse>> PublishPlanAsync(Guid id, PublishPlanRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Visibility))
            return ApiResult<PlanResponse>.Fail(new Error("bad_request", "visibility is required"), 400);
        var row = await plans.SetVisibilityAsync(id, req.Visibility.Trim());
        return row is null
            ? ApiResult<PlanResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<PlanResponse>.Ok(row.ToResponse());
    }

    public Task<IReadOnlyList<InvoiceResponse>> ListInvoicesAsync(string? status, Guid? tenantId, CancellationToken ct = default) =>
        invoices.ListAsync(status, tenantId);

    public async Task<ApiResult<InvoiceResponse>> GetInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var inv = await invoices.GetAsync(id);
        return inv is null
            ? ApiResult<InvoiceResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<InvoiceResponse>.Ok(inv);
    }

    public async Task<ApiResult<(byte[] Pdf, string FileName)>> GetInvoicePdfAsync(Guid id, CancellationToken ct = default)
    {
        var built = await BuildInvoicePdfAsync(id, ct);
        if (built.Error is not null)
            return ApiResult<(byte[], string)>.Fail(built.Error, built.StatusCode);
        var (pdf, fileName, _) = built.Data!;
        return ApiResult<(byte[], string)>.Ok((pdf, fileName));
    }

    public async Task<ApiResult> SendInvoiceEmailAsync(Guid id, CancellationToken ct = default)
    {
        var built = await BuildInvoicePdfAsync(id, ct);
        if (built.Error is not null)
            return ApiResult.Fail(built.Error, built.StatusCode);
        var (pdf, fileName, inv) = built.Data!;
        var client = await clients.GetAsync(inv.TenantId, ct);
        if (client is null)
            return ApiResult.Fail(new Error("not_found", "client not found"), 404);
        if (string.IsNullOrWhiteSpace(client.ContactEmail))
            return ApiResult.Fail(new Error("invalid_request", "school has no contact email"), 422);

        var amountText = inv.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var due = inv.Due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var owner = string.IsNullOrWhiteSpace(client.ContactName) ? "" : " " + client.ContactName;
        var body =
            $"Hello{owner},\n\n" +
            $"Please find attached the Catre Technology invoice for {client.Name}.\n\n" +
            $"School: {client.Name}\n" +
            $"Plan: {inv.PlanName ?? client.PlanName}\n" +
            $"Students: {client.StudentsCount}\n" +
            $"Amount: ₹{amountText}\n" +
            $"Due: {due}\n" +
            $"Status: {inv.Status}\n\n" +
            "Full plan, usage and billing details are in the PDF.\n\n" +
            "— Catre Technology";
        emailQueue.Enqueue(new EmailMessage(
            client.ContactEmail.Trim(),
            $"Catre Invoice — {client.Name} — ₹{amountText}",
            body,
            pdf,
            fileName,
            "application/pdf"));
        return ApiResult.Ok();
    }

    private async Task<ApiResult<(byte[] Pdf, string FileName, InvoiceResponse Invoice)>> BuildInvoicePdfAsync(
        Guid id, CancellationToken ct)
    {
        var inv = await invoices.GetAsync(id, ct);
        if (inv is null)
            return ApiResult<(byte[], string, InvoiceResponse)>.Fail(new Error("not_found", "resource not found"), 404);

        var client = await clients.GetAsync(inv.TenantId, ct);
        if (client is null)
            return ApiResult<(byte[], string, InvoiceResponse)>.Fail(new Error("not_found", "client not found"), 404);

        PlanRow? plan = null;
        if (client.PlanId is Guid planId)
            plan = await plans.GetAsync(planId, ct);

        var subs = await subscriptions.ListAsync(client.Id, "active", ct);
        var sub = subs.FirstOrDefault();

        var pdf = invoicePdf.Generate(InvoicePdfGenerator.From(inv, client, plan, sub));
        var fileName = $"Catre-Invoice-{inv.Id:N}.pdf";
        return ApiResult<(byte[], string, InvoiceResponse)>.Ok((pdf, fileName, inv));
    }

    public async Task<ApiResult<InvoiceResponse>> MarkInvoicePaidAsync(Guid id, CancellationToken ct = default)
    {
        if (await invoices.GetAsync(id) is null)
            return ApiResult<InvoiceResponse>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<InvoiceResponse>.Ok((await invoices.MarkPaidAsync(id))!);
    }

    public async Task<ApiResult<InvoiceResponse>> RefundInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var inv = await invoices.GetAsync(id);
        if (inv is null)
            return ApiResult<InvoiceResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (inv.Status != "paid")
            return ApiResult<InvoiceResponse>.Fail(new Error("conflict", "invoice is not paid"), 409);
        return ApiResult<InvoiceResponse>.Ok((await invoices.RefundAsync(id))!);
    }

    public Task<IReadOnlyList<SubscriptionResponse>> ListSubscriptionsAsync(string? status, Guid? tenantId, CancellationToken ct = default) =>
        subscriptions.ListAsync(tenantId, status);

    public async Task<ApiResult<SubscriptionResponse>> GetSubscriptionAsync(Guid id, CancellationToken ct = default)
    {
        var sub = await subscriptions.GetAsync(id);
        return sub is null
            ? ApiResult<SubscriptionResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<SubscriptionResponse>.Ok(sub);
    }

    public async Task<ApiResult<SubscriptionResponse>> CreateSubscriptionAsync(CreateSubscriptionRequest req, CancellationToken ct = default) =>
        ApiResult<SubscriptionResponse>.Ok((await subscriptions.CreateAsync(req))!, 201);

    public Task<DashboardOverview> GetDashboardOverviewAsync(CancellationToken ct = default) =>
        dashboard.OverviewAsync();

    public Task<IReadOnlyList<OnboardingItemResponse>> ListOnboardingAsync(string? stage, CancellationToken ct = default) =>
        onboarding.ListAsync(stage);

    public async Task<ApiResult<OnboardingItemResponse>> CreateOnboardingAsync(CreateOnboardingRequest req, CancellationToken ct = default)
    {
        var id = await onboarding.CreateAsync(req);
        return ApiResult<OnboardingItemResponse>.Ok((await onboarding.GetAsync(id))!, 201);
    }

    public async Task<ApiResult<OnboardingItemResponse>> AdvanceOnboardingAsync(Guid id, AdvanceRequest req, CancellationToken ct = default)
    {
        if (await onboarding.GetAsync(id) is null)
            return ApiResult<OnboardingItemResponse>.Fail(new Error("not_found", "resource not found"), 404);
        await onboarding.AdvanceAsync(id, req.Stage);
        return ApiResult<OnboardingItemResponse>.Ok((await onboarding.GetAsync(id))!);
    }

    public async Task<ApiResult<OnboardingItemResponse>> UpdateOnboardingChecklistAsync(Guid id, ChecklistRequest req, CancellationToken ct = default)
    {
        if (await onboarding.GetAsync(id) is null)
            return ApiResult<OnboardingItemResponse>.Fail(new Error("not_found", "resource not found"), 404);
        await onboarding.SetChecklistAsync(id, req.Label, req.Done);
        return ApiResult<OnboardingItemResponse>.Ok((await onboarding.GetAsync(id))!);
    }

    public Task<IReadOnlyList<TicketResponse>> ListTicketsAsync(string? status, string? q, CancellationToken ct = default) =>
        tickets.ListAsync(status, q);

    public async Task<ApiResult<TicketDetailResponse>> GetTicketAsync(Guid id, CancellationToken ct = default)
    {
        var detail = await tickets.GetDetailAsync(id);
        return detail is null
            ? ApiResult<TicketDetailResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<TicketDetailResponse>.Ok(detail);
    }

    public async Task<ApiResult<TicketDetailResponse>> CreateTicketAsync(CreateTicketRequest req, CancellationToken ct = default)
    {
        var id = await tickets.CreateAsync(req);
        return ApiResult<TicketDetailResponse>.Ok((await tickets.GetDetailAsync(id))!, 201);
    }

    public async Task<ApiResult<TicketResponse>> UpdateTicketAsync(Guid id, UpdateTicketRequest req, CancellationToken ct = default)
    {
        if (await tickets.GetDetailAsync(id) is null)
            return ApiResult<TicketResponse>.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult<TicketResponse>.Ok((await tickets.UpdateAsync(id, req.Status, req.Assignee))!);
    }

    public async Task<ApiResult<TicketDetailResponse>> AddTicketMessageAsync(Guid id, AddMessageRequest req, string actorId, CancellationToken ct = default)
    {
        if (await tickets.GetDetailAsync(id) is null)
            return ApiResult<TicketDetailResponse>.Fail(new Error("not_found", "resource not found"), 404);
        await tickets.AddMessageAsync(id, actorId, req.Text);
        return ApiResult<TicketDetailResponse>.Ok((await tickets.GetDetailAsync(id))!, 201);
    }

    public Task<IReadOnlyList<TeamMemberResponse>> ListTeamAsync(CancellationToken ct = default) =>
        team.ListAsync();

    public async Task<ApiResult<TeamMemberResponse>> InviteTeamMemberAsync(InviteTeamRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Role))
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "name, email, and role are required"), 422);
        if (string.IsNullOrWhiteSpace(req.EmployeeId))
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "employee_id is required"), 422);
        if (!CatreRoles.Contains(req.Role.Trim()))
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "role must be owner, admin, support, sales, finance, or analyst"), 422);
        if (req.PhotoUrl is { Length: > 400_000 })
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "photo is too large (max ~300KB)"), 422);
        if (req.PhotoUrl is { Length: > 0 } &&
            !req.PhotoUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
            !req.PhotoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !req.PhotoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "photo must be an image data URL or http(s) URL"), 422);

        var email = req.Email.Trim();
        var role = req.Role.Trim().ToLowerInvariant();
        var employeeId = req.EmployeeId.Trim();
        var existing = (await team.ListAsync(ct)).ToList();
        if (existing.Any(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)))
            return ApiResult<TeamMemberResponse>.Fail(new Error("conflict", "team member with this email already exists"), 409);
        if (existing.Any(m => string.Equals(m.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase)))
            return ApiResult<TeamMemberResponse>.Fail(new Error("conflict", "employee_id already in use"), 409);

        var userId = await platformUsers.FindPlatformUserIdByEmailAsync(email, ct);
        if (userId is null)
        {
            userId = await platformUsers.CreateUserAsync(
                tenantId: null, email: email, phone: string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim(),
                isPlatform: true, roles: [role], ct);
        }
        else
        {
            await platformUsers.ReplaceRolesAsync(userId.Value, [role], ct);
            await platformUsers.SetStatusAsync(userId.Value, "active", ct);
        }

        var member = await team.InviteAsync(new InviteTeamRequest(
            req.Name.Trim(), email, role, employeeId,
            string.IsNullOrWhiteSpace(req.PhotoUrl) ? null : req.PhotoUrl.Trim(),
            string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim()), ct);
        if (member is null)
            return ApiResult<TeamMemberResponse>.Fail(new Error("internal_error", "failed to create team member"), 500);

        var docError = await AttachDocumentsAsync(member.Id, req.Documents, ct);
        if (docError is not null)
            return ApiResult<TeamMemberResponse>.Fail(docError, 422);

        return ApiResult<TeamMemberResponse>.Ok((await team.GetAsync(member.Id, ct))!, 201);
    }

    public async Task<ApiResult<TeamMemberResponse>> UpdateTeamMemberAsync(Guid id, UpdateTeamRequest req, CancellationToken ct = default)
    {
        if (req.Role is { } role && !CatreRoles.Contains(role.Trim()))
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "role must be owner, admin, support, sales, finance, or analyst"), 422);
        if (req.PhotoUrl is { Length: > 400_000 })
            return ApiResult<TeamMemberResponse>.Fail(new Error("invalid_request", "photo is too large (max ~300KB)"), 422);

        var updated = await team.UpdateAsync(id, new UpdateTeamRequest(
            req.Role?.Trim().ToLowerInvariant(),
            req.Status?.Trim().ToLowerInvariant(),
            req.Name?.Trim(),
            req.EmployeeId?.Trim(),
            req.PhotoUrl,
            req.Phone?.Trim()));
        if (updated is null)
            return ApiResult<TeamMemberResponse>.Fail(new Error("not_found", "resource not found"), 404);

        var userId = await platformUsers.FindPlatformUserIdByEmailAsync(updated.Email, ct);
        if (userId is Guid uid)
        {
            if (req.Role is not null)
                await platformUsers.ReplaceRolesAsync(uid, [updated.Role], ct);
            if (req.Status is not null)
            {
                var userStatus = string.Equals(updated.Status, "active", StringComparison.OrdinalIgnoreCase)
                    ? "active" : "deactivated";
                await platformUsers.SetStatusAsync(uid, userStatus, ct);
            }
        }

        return ApiResult<TeamMemberResponse>.Ok(updated);
    }

    public async Task<ApiResult<TeamDocumentMeta>> AddTeamDocumentAsync(Guid memberId, TeamDocumentInput req, CancellationToken ct = default)
    {
        if (await team.GetAsync(memberId, ct) is null)
            return ApiResult<TeamDocumentMeta>.Fail(new Error("not_found", "resource not found"), 404);
        var err = ValidateDocument(req);
        if (err is not null)
            return ApiResult<TeamDocumentMeta>.Fail(err, 422);
        var size = EstimateContentBytes(req.Content);
        var meta = await team.AddDocumentAsync(memberId, NormalizeDocument(req), size, ct);
        return ApiResult<TeamDocumentMeta>.Ok(meta!, 201);
    }

    public async Task<ApiResult<TeamDocumentDetail>> GetTeamDocumentAsync(Guid memberId, Guid docId, CancellationToken ct = default)
    {
        var doc = await team.GetDocumentAsync(memberId, docId, ct);
        return doc is null
            ? ApiResult<TeamDocumentDetail>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<TeamDocumentDetail>.Ok(doc);
    }

    public async Task<ApiResult> DeleteTeamDocumentAsync(Guid memberId, Guid docId, CancellationToken ct = default)
    {
        if (!await team.DeleteDocumentAsync(memberId, docId, ct))
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult.NoContent();
    }

    private async Task<Error?> AttachDocumentsAsync(Guid memberId, IReadOnlyList<TeamDocumentInput>? docs, CancellationToken ct)
    {
        if (docs is null || docs.Count == 0) return null;
        if (docs.Count > 8)
            return new Error("invalid_request", "at most 8 documents allowed");
        foreach (var raw in docs)
        {
            var err = ValidateDocument(raw);
            if (err is not null) return err;
            var doc = NormalizeDocument(raw);
            var size = EstimateContentBytes(doc.Content);
            await team.AddDocumentAsync(memberId, doc, size, ct);
        }
        return null;
    }

    private static Error? ValidateDocument(TeamDocumentInput doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Label) || string.IsNullOrWhiteSpace(doc.FileName) || string.IsNullOrWhiteSpace(doc.Content))
            return new Error("invalid_request", "each document needs label, file_name, and content");
        if (doc.Content.Length > 3_500_000)
            return new Error("invalid_request", $"document '{doc.FileName}' is too large (max ~2.5MB)");
        if (!doc.Content.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return new Error("invalid_request", $"document '{doc.FileName}' content must be a data URL");
        return null;
    }

    private static TeamDocumentInput NormalizeDocument(TeamDocumentInput doc) =>
        new(
            doc.Label.Trim(),
            doc.FileName.Trim(),
            string.IsNullOrWhiteSpace(doc.ContentType) ? "application/octet-stream" : doc.ContentType.Trim(),
            doc.Content.Trim());

    private static int EstimateContentBytes(string content)
    {
        var comma = content.IndexOf(',');
        var b64 = comma >= 0 ? content[(comma + 1)..] : content;
        return (int)(b64.Length * 0.75);
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string? kind, Guid? actorId, Guid? tenantId, CancellationToken ct = default) =>
        audit.ListAsync(kind, actorId, tenantId);

    public Task<RevenueReport> GetRevenueReportAsync(CancellationToken ct = default) =>
        reports.RevenueAsync();

    public async Task<string> ExportClientsCsvAsync(CancellationToken ct = default)
    {
        var rows = await clients.ListAsync(null, null, null);
        var sb = new StringBuilder("client,status,plan,mrr,students,staff,country,created\n");
        foreach (var r in rows)
            sb.Append(Csv(r.Name)).Append(',').Append(r.Status).Append(',').Append(Csv(r.PlanName)).Append(',')
              .Append(r.Mrr).Append(',').Append(r.StudentsCount).Append(',').Append(r.StaffCount).Append(',')
              .Append(Csv(r.Country)).Append(',').Append(r.CreatedAt.ToString("yyyy-MM-dd")).Append('\n');
        return sb.ToString();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
