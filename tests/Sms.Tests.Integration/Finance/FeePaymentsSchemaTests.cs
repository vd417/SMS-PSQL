using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class FeePaymentsSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task FeePayments_has_idempotency_columns_and_unique_filtered_index()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var cols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'FeePayments'")).ToList();
        cols.Should().Contain(["CreatedAt", "UpdatedAt", "IdempotencyKey"]);

        var indexExists = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_FeePayments_Tenant_IdempotencyKey'");
        indexExists.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_within_tenant_is_rejected_by_the_index()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();
        // FeePayments has an RLS block predicate on TenantId (see M0019_Finance_Tables.cs:24-27) —
        // session context must be stamped to this tenant or the INSERT is blocked, not just the unique index.
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        var key = Guid.NewGuid();
        const string insert = """
            INSERT dbo.FeePayments (Id, TenantId, StudentId, FeeType, Amount, [Date], IdempotencyKey)
            VALUES (NEWID(), @tenantId, NEWID(), 'academic', 100, CAST(SYSUTCDATETIME() AS date), @key)
            """;
        await conn.ExecuteAsync(insert, new { tenantId, key });

        var act = () => conn.ExecuteAsync(insert, new { tenantId, key });
        await act.Should().ThrowAsync<SqlException>();
    }
}
