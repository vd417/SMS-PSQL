using Dapper;
using FluentAssertions;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Tenancy;

[Collection("sql")]
public class AuditRepositoryTests(PostgresFixture fx)
{
    private async Task<Guid> SeedTenantAsync()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var id = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\", \"Tier\") VALUES (@id,'T',@s,'active','gold')",
            new { id, s = $"t{id:N}" });
        return id;
    }

    private AuditRepository Repo()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        return new AuditRepository(new NpgsqlConnectionFactory(fx.ConnectionString, ctx));
    }

    [Fact]
    public async Task ListForSchoolAsync_filters_by_tenant_and_paginates()
    {
        var tenantId = await SeedTenantAsync();
        var otherTenantId = await SeedTenantAsync();
        var repo = Repo();

        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(Guid.NewGuid(), "Actor", "school.admin", "user.role_changed", $"target-{i}", "identity", tenantId);
        await repo.InsertAsync(Guid.NewGuid(), "Other", "school.admin", "user.role_changed", "other-target", "identity", otherTenantId);

        var (page1, cursor1) = await repo.ListForSchoolAsync(tenantId, null, null, null, null, null, pageSize: 2);
        page1.Should().HaveCount(2);
        cursor1.Should().NotBeNull();

        var (page2, cursor2) = await repo.ListForSchoolAsync(tenantId, null, null, null, null, cursor1, pageSize: 2);
        page2.Should().ContainSingle();
        cursor2.Should().BeNull();
    }

    [Fact]
    public async Task ListForSchoolAsync_ignores_malformed_cursor_and_returns_first_page()
    {
        var tenantId = await SeedTenantAsync();
        var repo = Repo();

        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(Guid.NewGuid(), "Actor", "school.admin", "user.role_changed", $"target-{i}", "identity", tenantId);

        var (pageNoCursor, cursorFromNull) = await repo.ListForSchoolAsync(tenantId, null, null, null, null, null, pageSize: 2);
        var (pageGarbageCursor, cursorFromGarbage) = await repo.ListForSchoolAsync(tenantId, null, null, null, null, "not-a-valid-cursor", pageSize: 2);

        pageGarbageCursor.Should().HaveCount(pageNoCursor.Count);
        pageGarbageCursor.Select(r => r.Id).Should().Equal(pageNoCursor.Select(r => r.Id));
        cursorFromGarbage.Should().Be(cursorFromNull);
    }
}
