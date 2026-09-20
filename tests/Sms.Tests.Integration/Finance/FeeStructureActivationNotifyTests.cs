using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Common;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class FeeStructureActivationNotifyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingAnnouncementService : IAnnouncementService
    {
        public List<CreateAnnouncementRequest> Created { get; } = [];

        public Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AnnouncementResponse>>.Ok(Array.Empty<AnnouncementResponse>()));

        public Task<ApiResult<AnnouncementResponse>> CreateAsync(
            CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default)
        {
            Created.Add(req);
            return Task.FromResult(ApiResult<AnnouncementResponse>.Ok(
                new AnnouncementResponse(Guid.NewGuid(), Guid.Empty, req.Title, req.Body, DateTime.UtcNow, null, role, req.Type ?? "general", false, req.Audience)));
        }
    }

    private static WebApplicationFactory<Program> BuildApp(PostgresFixture fx, CapturingAnnouncementService fake) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddScoped<IAnnouncementService>(_ => fake));
        });

    private static async Task<(Guid tenantId, Guid principalUserId)> SeedTenantAsync(PostgresFixture fx)
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        return (tenantId, principalUserId);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static object StructureBody(string status) => new
    {
        name = "Standard",
        academic_year = "2026",
        currency = "INR",
        effective_from = "2026-01-01",
        status,
        amounts = new Dictionary<string, object> { ["5"] = new Dictionary<string, object> { ["Tuition"] = 8500 } },
    };

    [Fact]
    public async Task First_activation_from_no_structure_fires_one_in_app_only_notification()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId) = await SeedTenantAsync(fx);
        var client = Client(app, tenantId, principalUserId);

        var resp = await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("active"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle();
        var notify = fake.Created[0];
        notify.Title.Should().Be("Fee Structure Published");
        notify.Audience.Should().Be("parents");
        notify.Channels.Should().BeEquivalentTo(new[] { "app" });
        notify.Body.Should().Contain("2026");
    }

    [Fact]
    public async Task Saving_as_inactive_never_notifies()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId) = await SeedTenantAsync(fx);
        var client = Client(app, tenantId, principalUserId);

        var resp = await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("inactive"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Resaving_while_already_active_does_not_notify_again()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId) = await SeedTenantAsync(fx);
        var client = Client(app, tenantId, principalUserId);

        var first = await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("active"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Created.Should().ContainSingle();

        var second = await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("active"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle("re-saving while already active must not re-notify");
    }

    [Fact]
    public async Task Reactivating_after_going_inactive_notifies_again()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId) = await SeedTenantAsync(fx);
        var client = Client(app, tenantId, principalUserId);

        (await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("active"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("inactive"))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Created.Should().ContainSingle("only the first activation should have notified so far");

        (await client.PutAsJsonAsync("/v1/fees/structure", StructureBody("active"))).StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().HaveCount(2, "reactivation after going inactive is a genuine re-publish");
    }
}
