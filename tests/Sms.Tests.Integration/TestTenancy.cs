using Dapper;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration;

/// <summary>Seed dbo.Tenants for integration tests that need a plan tier.</summary>
public static class TestTenancy
{
    public static async Task EnsureTenantAsync(string connectionString, Guid tenantId,
        string tier = "silver", string status = "active")
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(connectionString, ctx);
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier")
            VALUES (@tenantId, @name, @slug, @status, @tier)
            ON CONFLICT ("Id") DO UPDATE SET "Tier" = @tier, "Status" = @status
            """,
            new
            {
                tenantId,
                name = "Integration Test",
                slug = $"t{tenantId:N}",
                status,
                tier,
            });
    }
}
