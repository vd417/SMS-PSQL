using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Users;

[Collection("sql")]
public class PrincipalSuspensionTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId, string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private async Task EnsureTenantAsync(Guid tenantId)
    {
        var factory = PlatformFactory();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync("INSERT dbo.Tenants (Id, Name, Slug, Status) VALUES (@t,'T',@s,'active')",
            new { t = tenantId, s = "t-" + tenantId.ToString("N") });
    }

    private async Task<Guid> SeedUserAsync(Guid tenantId, string role, string status = "active")
    {
        var factory = PlatformFactory();
        await using var c = await factory.OpenAsync();
        var userId = await c.QuerySingleAsync<Guid>(
            "INSERT dbo.Users (Id, TenantId, Email, Status, IsPlatform) OUTPUT inserted.Id VALUES (NEWID(),@t,@e,@st,0)",
            new { t = tenantId, e = $"u{Guid.NewGuid():N}@x.com", st = status });
        await c.ExecuteAsync("INSERT dbo.UserRoles (UserId, Role) VALUES (@u,@r)", new { u = userId, r = role });
        return userId;
    }

    private async Task<Guid> SeedUserAsync(Guid tenantId, string[] roles, string status = "active")
    {
        var factory = PlatformFactory();
        await using var c = await factory.OpenAsync();
        var userId = await c.QuerySingleAsync<Guid>(
            "INSERT dbo.Users (Id, TenantId, Email, Status, IsPlatform) OUTPUT inserted.Id VALUES (NEWID(),@t,@e,@st,0)",
            new { t = tenantId, e = $"u{Guid.NewGuid():N}@x.com", st = status });
        foreach (var role in roles)
            await c.ExecuteAsync("INSERT dbo.UserRoles (UserId, Role) VALUES (@u,@r)", new { u = userId, r = role });
        return userId;
    }

    [Fact]
    public async Task Principal_can_suspend_a_teacher()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, Policies.Teacher);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Principal_can_suspend_staff()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, Policies.Staff);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Principal_cannot_suspend_an_admin()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, Policies.SchoolAdmin);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_can_still_suspend_an_admin_account_unchanged()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, Policies.SchoolAdmin);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.SchoolAdmin]);
        var resp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Principal_listing_users_only_sees_teacher_and_staff_rows()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        await SeedUserAsync(tenantId, Policies.SchoolAdmin);
        var teacherId = await SeedUserAsync(tenantId, Policies.Teacher);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.GetAsync("/v1/users");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToArray();
        ids.Should().BeEquivalentTo([teacherId.ToString()]);
    }

    [Fact]
    public async Task Admin_listing_users_still_sees_every_role_unchanged()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var adminId = await SeedUserAsync(tenantId, Policies.SchoolAdmin);
        var teacherId = await SeedUserAsync(tenantId, Policies.Teacher);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.SchoolAdmin]);
        var resp = await client.GetAsync("/v1/users");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToArray();
        ids.Should().BeEquivalentTo([adminId.ToString(), teacherId.ToString()]);
    }

    [Fact]
    public async Task Principal_cannot_suspend_a_teacher_who_is_also_admin()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, [Policies.SchoolAdmin, Policies.Teacher]);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_listing_excludes_a_teacher_who_is_also_admin()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        await SeedUserAsync(tenantId, [Policies.SchoolAdmin, Policies.Teacher]);
        var teacherId = await SeedUserAsync(tenantId, Policies.Teacher);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Principal]);
        var resp = await client.GetAsync("/v1/users");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToArray();
        ids.Should().BeEquivalentTo([teacherId.ToString()]);
    }

    [Fact]
    public async Task Teacher_actor_cannot_call_suspend_or_list_endpoints()
    {
        var tenantId = Guid.NewGuid();
        await EnsureTenantAsync(tenantId);
        var targetId = await SeedUserAsync(tenantId, Policies.Teacher);

        await using var app = App();
        var client = TenantClient(app, tenantId, [Policies.Teacher]);

        var statusResp = await client.PutAsJsonAsync($"/v1/users/{targetId}/status", new { active = false });
        statusResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var listResp = await client.GetAsync("/v1/users");
        listResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
