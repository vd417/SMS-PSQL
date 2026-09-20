using Dapper;
using FluentAssertions;
using Sms.Application.DTOs.Users;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.DAO;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class RoleTemplateDaoTests(PostgresFixture fx)
{
    private async Task<Guid> SeedTenantAsync()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var tenantId = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@id,'T',@s,'active','gold')""",
            new { id = tenantId, s = $"t{tenantId:N}" });
        return tenantId;
    }

    private IRoleTemplateDao Dao()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        return new RoleTemplateDao(new NpgsqlConnectionFactory(fx.ConnectionString, ctx));
    }

    [Fact]
    public async Task SetAsync_then_GetAsync_round_trips_overrides()
    {
        var tenantId = await SeedTenantAsync();
        var dao = Dao();

        await dao.SetAsync(tenantId, [new RoleTemplateOverrideDto("teacher", "fees", "E", "grant")]);
        var rows = await dao.GetAsync(tenantId);

        rows.Should().ContainSingle(r =>
            r.Role == "teacher" && r.Module == "fees" && r.Cap == "E" && r.Effect == "grant");
    }

    [Fact]
    public async Task SetAsync_replaces_the_full_set_for_a_tenant()
    {
        var tenantId = await SeedTenantAsync();
        var dao = Dao();

        await dao.SetAsync(tenantId, [
            new RoleTemplateOverrideDto("teacher", "fees", "E", "grant"),
            new RoleTemplateOverrideDto("staff", "sis", "V", "grant"),
        ]);
        await dao.SetAsync(tenantId, [new RoleTemplateOverrideDto("teacher", "fees", "E", "grant")]);

        var rows = await dao.GetAsync(tenantId);
        rows.Should().ContainSingle();
    }
}
