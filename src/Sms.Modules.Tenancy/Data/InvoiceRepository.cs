using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class InvoiceRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"TenantName\", \"PlanName\", \"Amount\", \"Status\", \"Issued\", \"Due\", \"PaidOn\"";

    public Task<InvoiceResponse?> CreateAsync(CreateInvoiceRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<InvoiceResponse>("dbo.Invoice_Create",
            new { r.TenantId, r.TenantName, r.PlanName, r.Amount, r.Due }, ct);

    public async Task<InvoiceResponse?> SetAmountAsync(Guid id, decimal amount, CancellationToken ct = default) =>
        (await QueryInlineAsync<InvoiceResponse>(
            $"UPDATE \"dbo\".\"Invoices\" SET \"Amount\" = @amount WHERE \"Id\" = @id; SELECT {Cols} FROM \"dbo\".\"Invoices\" WHERE \"Id\" = @id;",
            new { id, amount }, ct)).FirstOrDefault();

    public async Task<InvoiceResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<InvoiceResponse>($"SELECT {Cols} FROM \"dbo\".\"Invoices\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<InvoiceResponse>> ListAsync(
        string? status, Guid? tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<InvoiceResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"Invoices\" WHERE (@status IS NULL OR \"Status\" = @status) " +
            "AND (@tenantId IS NULL OR \"TenantId\" = @tenantId) ORDER BY \"Issued\" DESC",
            new { status, tenantId }, ct);

    public Task<InvoiceResponse?> MarkPaidAsync(Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<InvoiceResponse>("dbo.Invoice_MarkPaid", new { Id = id }, ct);

    public Task<InvoiceResponse?> RefundAsync(Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<InvoiceResponse>("dbo.Invoice_Refund", new { Id = id }, ct);

    /// <summary>Tag an invoice as a mid-cycle seat proration charge and record the seats billed.</summary>
    public Task<int> MarkProrationAsync(Guid id, int seats, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """UPDATE "dbo"."Invoices" SET "Kind" = 'proration', "Seats" = @seats WHERE "Id" = @id""",
            new { id, seats }, ct);

    /// <summary>True when a recurring cycle invoice already covers this tenant's period (idempotency guard).</summary>
    public async Task<bool> CycleInvoiceExistsAsync(Guid tenantId, DateTime periodStart, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM "dbo"."Invoices"
                WHERE "TenantId" = @tid AND "Kind" = 'cycle' AND "PeriodStart" = @periodStart
            ) THEN 1 ELSE 0 END
            """,
            new { tid = tenantId, periodStart }, ct)).FirstOrDefault() == 1;

    /// <summary>Stamp the billed period window and seat count on a recurring cycle invoice.</summary>
    public Task<int> SetCyclePeriodAsync(
        Guid id, DateTime periodStart, DateTime periodEnd, int seats, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."Invoices"
            SET "Kind" = 'cycle', "PeriodStart" = @periodStart, "PeriodEnd" = @periodEnd, "Seats" = @seats
            WHERE "Id" = @id
            """,
            new { id, periodStart, periodEnd, seats }, ct);
}
