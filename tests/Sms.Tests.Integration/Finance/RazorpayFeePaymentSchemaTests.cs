using Microsoft.Data.SqlClient;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class RazorpayFeePaymentSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task TenantPaymentCredentials_and_FeePaymentOrders_tables_exist_with_expected_columns()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var credColumns = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'TenantPaymentCredentials'")).ToList();
        credColumns.Should().Contain(new[]
        {
            "TenantId", "Provider", "KeyId", "KeySecretEncrypted", "WebhookSecretEncrypted",
            "Mode", "IsEnabled", "CreatedAt", "UpdatedAt",
        });

        var orderColumns = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'FeePaymentOrders'")).ToList();
        orderColumns.Should().Contain(new[]
        {
            "Id", "TenantId", "InvoiceId", "RazorpayOrderId", "AmountPaise", "Status",
            "InitiatedBy", "CreatedAt", "UpdatedAt",
        });

        var uniqueIndexes = (await conn.QueryAsync<string>(
            "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id " +
            "WHERE t.name = 'FeePaymentOrders' AND i.is_unique = 1")).ToList();
        uniqueIndexes.Should().Contain(n => n.Contains("RazorpayOrderId"));
    }
}
