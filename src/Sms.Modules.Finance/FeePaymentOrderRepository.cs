using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Finance;

public sealed record FeePaymentOrderRow(
    Guid Id, Guid TenantId, Guid InvoiceId, string RazorpayOrderId, long AmountPaise, string Status, string InitiatedBy);

public sealed class FeePaymentOrderRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task CreateAsync(
        Guid id, Guid tenantId, Guid invoiceId, string razorpayOrderId, long amountPaise, string initiatedBy,
        CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "INSERT INTO \"dbo\".\"FeePaymentOrders\" (\"Id\", \"TenantId\", \"InvoiceId\", \"RazorpayOrderId\", \"AmountPaise\", \"Status\", \"InitiatedBy\") " +
            "VALUES (@id, @tenantId, @invoiceId, @razorpayOrderId, @amountPaise, 'Created', @initiatedBy)",
            new { id, tenantId, invoiceId, razorpayOrderId, amountPaise, initiatedBy }, ct);

    public async Task<FeePaymentOrderRow?> GetByOrderIdAsync(string razorpayOrderId, CancellationToken ct = default) =>
        (await QueryInlineAsync<FeePaymentOrderRow>(
            "SELECT \"Id\", \"TenantId\", \"InvoiceId\", \"RazorpayOrderId\", \"AmountPaise\", \"Status\", \"InitiatedBy\" FROM \"dbo\".\"FeePaymentOrders\" " +
            "WHERE \"RazorpayOrderId\" = @razorpayOrderId", new { razorpayOrderId }, ct)).FirstOrDefault();

    public Task MarkStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "UPDATE \"dbo\".\"FeePaymentOrders\" SET \"Status\" = @status, \"UpdatedAt\" = now() WHERE \"Id\" = @id",
            new { id, status }, ct);
}
