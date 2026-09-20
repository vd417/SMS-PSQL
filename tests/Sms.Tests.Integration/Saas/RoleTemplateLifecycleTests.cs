using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class RoleTemplateLifecycleTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["school.admin"], isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private async Task<Guid> SeedActiveTenantAsync()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var id = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync("INSERT dbo.Tenants (Id, Name, Slug, Status, Tier) VALUES (@id,'T',@s,'active','gold')",
            new { id, s = $"t{id:N}" });
        return id;
    }

    private async Task<int> CountAuditRowsAsync(Guid tenantId, string action)
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        return await c.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.AuditLog WHERE TenantId = @tenantId AND Action = @action",
            new { tenantId, action });
    }

    [Fact]
    public async Task Set_then_get_round_trips_and_writes_an_audit_row()
    {
        var tenantId = await SeedActiveTenantAsync();
        await using var app = App();
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        var put = await admin.PutAsJsonAsync("/v1/roles/permissions", new
        {
            overrides = new[] { new { role = "teacher", module = "fees", cap = "E", effect = "grant" } },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await admin.GetFromJsonAsync<RoleTemplateEnvelope>("/v1/roles/permissions");
        get!.Data.Should().ContainSingle(r => r.Role == "teacher" && r.Module == "fees" && r.Cap == "E" && r.Effect == "grant");

        (await CountAuditRowsAsync(tenantId, "role_template.updated")).Should().Be(1);
    }

    [Fact]
    public async Task Owner_role_rows_are_silently_dropped()
    {
        var tenantId = await SeedActiveTenantAsync();
        await using var app = App();
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        await admin.PutAsJsonAsync("/v1/roles/permissions", new
        {
            overrides = new[] { new { role = "owner", module = "fees", cap = "A", effect = "grant" } },
        });

        var get = await admin.GetFromJsonAsync<RoleTemplateEnvelope>("/v1/roles/permissions");
        get!.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task School_audit_endpoint_returns_only_the_caller_tenants_rows()
    {
        var tenantId = await SeedActiveTenantAsync();
        var otherTenantId = await SeedActiveTenantAsync();
        await using var app = App();
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        await admin.PutAsJsonAsync("/v1/roles/permissions", new
        {
            overrides = new[] { new { role = "teacher", module = "fees", cap = "E", effect = "grant" } },
        });

        var otherAdmin = AdminClient(app, otherTenantId, Guid.NewGuid());
        await otherAdmin.PutAsJsonAsync("/v1/roles/permissions", new
        {
            overrides = new[] { new { role = "staff", module = "sis", cap = "V", effect = "grant" } },
        });

        var res = await admin.GetFromJsonAsync<AuditEnvelope>("/v1/school/audit?action=role_template.updated");
        res!.Data.Should().ContainSingle();
    }

    private sealed record RoleTemplateOverrideWire(string Role, string Module, string Cap, string Effect);
    private sealed record RoleTemplateEnvelope(RoleTemplateOverrideWire[] Data);
    private sealed record AuditRow(string Id, string? ActorId, string? ActorName, string Action, string? Target, string At);
    private sealed record AuditEnvelope(AuditRow[] Data, string? NextCursor);
}
