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
public class OwnerPolicyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), Guid.NewGuid(), roles, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Owner_role_has_admin_level_access_to_approvals()
    {
        await using var app = App();
        var client = TenantClient(app, [Policies.SchoolOwner]);
        (await client.GetAsync("/v1/approvals")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_role_is_not_platform()
    {
        await using var app = App();
        var client = TenantClient(app, [Policies.SchoolOwner]);
        // /v1/clients is platform-only; a school owner must be forbidden.
        (await client.GetAsync("/v1/clients")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_can_invite_a_school_user()
    {
        await using var app = App();
        var client = TenantClient(app, [Policies.SchoolOwner]);
        var resp = await client.PostAsJsonAsync("/v1/users",
            new { email = $"t{Guid.NewGuid():N}@x.com", phone = (string?)null, roles = new[] { Policies.Teacher } });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Owner_can_invite_co_owner_admin_and_principal()
    {
        await using var app = App();
        var client = TenantClient(app, [Policies.SchoolOwner]);
        foreach (var role in new[] { Policies.SchoolOwner, Policies.SchoolAdmin, Policies.Principal })
        {
            var resp = await client.PostAsJsonAsync("/v1/users",
                new { email = $"t{Guid.NewGuid():N}@x.com", phone = (string?)null, roles = new[] { role } });
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Admin_cannot_invite_school_owner()
    {
        await using var app = App();
        var client = TenantClient(app, [Policies.SchoolAdmin]);
        var resp = await client.PostAsJsonAsync("/v1/users",
            new { email = $"t{Guid.NewGuid():N}@x.com", phone = (string?)null, roles = new[] { Policies.SchoolOwner } });
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
