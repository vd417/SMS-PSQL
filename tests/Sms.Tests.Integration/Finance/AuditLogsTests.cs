using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Audit;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class AuditLogsTests(PostgresFixture fx)
{
    [Fact]
    public async Task AuditLogs_table_exists_with_expected_columns()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var cols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'AuditLogs'")).ToList();
        cols.Should().Contain([
            "Id", "TenantId", "ActorUserId", "Action", "Module", "EntityType", "EntityId",
            "TimestampUtc", "BeforeData", "AfterData",
        ]);
    }

    /// <summary>Stamps SESSION_CONTEXT the same way NpgsqlConnectionFactory does in production
    /// (see src/Sms.Shared.Kernel/Data/NpgsqlConnectionFactory.cs:19-29), so the AuditLogs RLS
    /// filter/block predicate (rls.fn_tenant_predicate) matches this tenant.</summary>
    private static async Task StampTenantAsync(SqlConnection conn, Guid tenantId)
    {
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@v", new { v = tenantId });
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=@v", new { v = 0 });
    }

    [Fact]
    public async Task AuditLogger_writes_row_inside_caller_transaction_and_commits_with_it()
    {
        var logger = new AuditLogger();
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await StampTenantAsync(conn, tenantId);
        await using var tx = await conn.BeginTransactionAsync();
        await logger.LogAsync(conn, tx, new AuditEntry(
            tenantId, actorId, "Test.Action", "Fees", "FeePayment", "entity-1",
            AfterData: new { amount = 100 }), CancellationToken.None);
        await tx.CommitAsync();

        var row = await conn.QuerySingleAsync<(Guid TenantId, Guid? ActorUserId, string Action, string Module, string EntityType, string EntityId)>(
            "SELECT TenantId, ActorUserId, Action, Module, EntityType, EntityId FROM dbo.AuditLogs WHERE TenantId = @tenantId",
            new { tenantId });
        row.ActorUserId.Should().Be(actorId);
        row.Action.Should().Be("Test.Action");
        row.Module.Should().Be("Fees");
        row.EntityType.Should().Be("FeePayment");
        row.EntityId.Should().Be("entity-1");
    }

    [Fact]
    public async Task AuditLogger_write_is_rolled_back_with_its_transaction()
    {
        var logger = new AuditLogger();
        var tenantId = Guid.NewGuid();

        await using (var conn = new SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await StampTenantAsync(conn, tenantId);
            await using var tx = await conn.BeginTransactionAsync();
            await logger.LogAsync(conn, tx, new AuditEntry(
                tenantId, null, "Test.RolledBack", "Fees", "FeePayment", "entity-2"), CancellationToken.None);
            await tx.RollbackAsync();
        }

        await using var verifyConn = new SqlConnection(fx.ConnectionString);
        await verifyConn.OpenAsync();
        await StampTenantAsync(verifyConn, tenantId);
        var count = await verifyConn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.AuditLogs WHERE TenantId = @tenantId", new { tenantId });
        count.Should().Be(0);
    }
}
