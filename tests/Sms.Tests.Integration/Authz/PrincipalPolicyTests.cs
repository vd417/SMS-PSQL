using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Authz;

[Collection("sql")]
public class PrincipalPolicyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, roles, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Teacher_role_is_forbidden_on_approvals()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Teacher]);
        (await client.GetAsync("/v1/approvals")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_role_can_read_approvals()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Principal]);
        (await client.GetAsync("/v1/approvals")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Teacher_role_is_forbidden_on_announcement_broadcast()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Teacher]);
        var resp = await client.PostAsJsonAsync("/v1/announcements",
            new { title = "x", body = "y", type = "info" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Teacher_role_is_forbidden_on_patch_approval()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Teacher]);
        var resp = await client.PatchAsJsonAsync($"/v1/approvals/{Guid.NewGuid()}",
            new { status = "approved" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
