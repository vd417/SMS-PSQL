using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class PlanUpgradeRequestRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<PlanUpgradeRequestResponse?> CreateAsync(
        Guid tenantId, Guid? fromPlanId, Guid toPlanId, decimal amount, string currency,
        string mode, string status, Guid? requestedByUserId, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_Create", new
        {
            TenantId = tenantId,
            FromPlanId = fromPlanId,
            ToPlanId = toPlanId,
            Amount = amount,
            Currency = currency,
            Mode = mode,
            Status = status,
            RequestedByUserId = requestedByUserId,
        }, ct);

    public Task<PlanUpgradeRequestResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_Get", new { Id = id }, ct);

    public Task<PlanUpgradeRequestResponse?> GetByOrderAsync(string razorpayOrderId, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_GetByOrder",
            new { RazorpayOrderId = razorpayOrderId }, ct);

    public Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListAsync(string? status, CancellationToken ct = default) =>
        QueryProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_List", new { Status = status }, ct);

    public Task<IReadOnlyList<PlanUpgradeRequestResponse>> ListByTenantsAsync(
        IReadOnlyList<Guid> tenantIds, CancellationToken ct = default)
    {
        if (tenantIds.Count == 0)
            return Task.FromResult<IReadOnlyList<PlanUpgradeRequestResponse>>([]);
        var csv = string.Join(',', tenantIds);
        return QueryProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_ListByTenants",
            new { TenantIds = csv }, ct);
    }

    /// Moves a request to approved only if it is still awaiting approval (paid online or pending
    /// offline), in one conditional UPDATE. Exactly one of several concurrent approvals wins; the
    /// others get null, so approval side effects (email, audit) happen once.
    public async Task<PlanUpgradeRequestResponse?> TryApproveAsync(
        Guid id, Guid? reviewedByUserId, CancellationToken ct = default)
    {
        var claimed = await ExecuteInlineAsync(
            """
            UPDATE "dbo"."PlanUpgradeRequests" SET
                "Status" = @approved,
                "ReviewedByUserId" = COALESCE(@reviewedByUserId, "ReviewedByUserId"),
                "UpdatedAt" = now()
            WHERE "Id" = @id AND "Status" = ANY(@awaiting)
            """,
            new
            {
                id,
                reviewedByUserId,
                approved = PlanUpgradeStatuses.Approved,
                awaiting = new[] { PlanUpgradeStatuses.PaidPendingApproval, PlanUpgradeStatuses.PendingOffline },
            }, ct);
        return claimed == 1 ? await GetAsync(id, ct) : null;
    }

    /// Undoes <see cref="TryApproveAsync"/> when the approval's follow-up steps fail, returning the
    /// request to the status it was claimed from so the approval can be retried.
    public Task<int> ReleaseApprovalAsync(Guid id, string awaitingStatus, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."PlanUpgradeRequests" SET "Status" = @awaitingStatus, "UpdatedAt" = now()
            WHERE "Id" = @id AND "Status" = @approved
            """,
            new { id, awaitingStatus, approved = PlanUpgradeStatuses.Approved }, ct);

    public Task<PlanUpgradeRequestResponse?> SetStatusAsync(
        Guid id, string status, Guid? reviewedByUserId, string? notes, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_SetStatus", new
        {
            Id = id,
            Status = status,
            ReviewedByUserId = reviewedByUserId,
            Notes = notes,
        }, ct);

    public Task<PlanUpgradeRequestResponse?> AttachRazorpayAsync(
        Guid id, string? orderId, string? paymentId, string? status, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_AttachRazorpay", new
        {
            Id = id,
            RazorpayOrderId = orderId,
            RazorpayPaymentId = paymentId,
            Status = status,
        }, ct);

    public Task<PlanUpgradeRequestResponse?> AttachInvoiceAsync(Guid id, Guid invoiceId, CancellationToken ct = default) =>
        QuerySingleProcAsync<PlanUpgradeRequestResponse>("dbo.PlanUpgradeRequest_AttachInvoice",
            new { Id = id, InvoiceId = invoiceId }, ct);
}
