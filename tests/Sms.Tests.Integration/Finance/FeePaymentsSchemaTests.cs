using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class FeePaymentsSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task FeePayments_has_idempotency_columns_and_unique_filtered_index()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var cols = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'dbo' AND table_name = 'FeePayments'")).ToList();
        cols.Should().Contain(["CreatedAt", "UpdatedAt", "IdempotencyKey"]);

        var indexExists = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'UX_FeePayments_Tenant_IdempotencyKey'");
        indexExists.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_within_tenant_is_rejected_by_the_index()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();
        // FeePayments has an RLS block predicate on TenantId (see M0019_Finance_Tables.cs:24-27) —
        // session context must be stamped to this tenant or the INSERT is blocked, not just the unique index.
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var key = Guid.NewGuid();
        const string insert = """
            INSERT INTO "dbo"."FeePayments" ("Id", "TenantId", "StudentId", "FeeType", "Amount", "Date", "IdempotencyKey")
            VALUES (gen_random_uuid(), @tenantId, gen_random_uuid(), 'academic', 100, now()::date, @key)
            """;
        await conn.ExecuteAsync(insert, new { tenantId, key });

        var act = () => conn.ExecuteAsync(insert, new { tenantId, key });
        await act.Should().ThrowAsync<PostgresException>();
    }
}
