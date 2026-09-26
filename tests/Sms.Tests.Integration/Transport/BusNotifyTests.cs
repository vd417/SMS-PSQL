using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusNotifyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<NpgsqlConnection> Open(string cs, Guid tenantId)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    /// Bus with two riders at two different stops, each with a parent login. Returns ids.
    private async Task<(Guid busId, Guid stopA, Guid parentA, Guid parentB)> SeedBus(Guid tenantId)
    {
        await using var conn = await Open(fx.ConnectionString, tenantId);
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopA = Guid.NewGuid();
        var stopB = Guid.NewGuid();
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteName\") VALUES (@Id, @TenantId, 'KA-07', 'North')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'North')",
            new { Id = routeId, TenantId = tenantId });
        foreach (var (stop, seq, name) in new[] { (stopA, 1, "Gate A"), (stopB, 2, "Gate B") })
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"RouteStops\" (\"Id\", \"TenantId\", \"RouteId\", \"Name\", \"Seq\", \"Lat\", \"Lng\") VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, 12.9, 77.6)",
                new { Id = stop, TenantId = tenantId, RouteId = routeId, Name = name, Seq = seq });
        foreach (var (stop, parent, name) in new[] { (stopA, parentA, "Asha"), (stopB, parentB, "Bala") })
        {
            var studentId = Guid.NewGuid();
            var adm = $"ADM-{Guid.NewGuid():N}"[..14];
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"GuardianPhone\") VALUES (@Id, @TenantId, @A, @N, '9876543210')",
                new { Id = studentId, TenantId = tenantId, A = adm, N = name });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\", \"TenantId\", \"StudentId\", \"BusId\", \"RouteId\", \"StopId\") VALUES (@Id, @TenantId, @S, @B, @R, @Stop)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = studentId, B = busId, R = routeId, Stop = stop });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"StudentId\", \"IsPlatform\", \"Status\") VALUES (@Id, @TenantId, @Adm, false, 'active')",
                new { Id = parent, TenantId = tenantId, Adm = adm });
        }
        return (busId, stopA, parentA, parentB);
    }

    private async Task<int> NoticeCount(Guid tenantId, Guid userId)
    {
        await using var conn = await Open(fx.ConnectionString, tenantId);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"Notifications\" WHERE \"UserId\" = @userId AND \"Icon\" = 'bus'", new { userId });
    }

    [Fact]
    public async Task Push_notifies_every_riders_parent_and_reports_reach()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, _, parentA, parentB) = await SeedBus(tenantId);

        var body = await Body(await Client(app, tenantId, Policies.Principal).PostAsJsonAsync(
            $"/v1/transport/buses/{busId}/notify", new { event_type = "departed", channels = new[] { "push" } }),
            HttpStatusCode.OK);

        body.GetProperty("data").GetProperty("reach").GetInt32().Should().Be(2);
        (await NoticeCount(tenantId, parentA)).Should().Be(1);
        (await NoticeCount(tenantId, parentB)).Should().Be(1);
    }

    [Fact]
    public async Task Stop_filter_only_notifies_riders_at_that_stop()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, stopA, parentA, parentB) = await SeedBus(tenantId);

        var body = await Body(await Client(app, tenantId, Policies.Principal).PostAsJsonAsync(
            $"/v1/transport/buses/{busId}/notify",
            new { event_type = "approaching", stop_id = stopA, channels = new[] { "push", "sms" } }),
            HttpStatusCode.OK);

        body.GetProperty("data").GetProperty("reach").GetInt32().Should().Be(2); // one push + one sms
        (await NoticeCount(tenantId, parentA)).Should().Be(1);
        (await NoticeCount(tenantId, parentB)).Should().Be(0);
    }

    [Fact]
    public async Task Rejects_unknown_event_type_and_empty_channels()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, _, _, _) = await SeedBus(tenantId);
        var admin = Client(app, tenantId, Policies.Principal);

        var bad = await Body(await admin.PostAsJsonAsync($"/v1/transport/buses/{busId}/notify",
            new { event_type = "exploded", channels = new[] { "push" } }), HttpStatusCode.UnprocessableEntity);
        bad.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_event_type");

        var none = await Body(await admin.PostAsJsonAsync($"/v1/transport/buses/{busId}/notify",
            new { event_type = "arrived", channels = Array.Empty<string>() }), HttpStatusCode.UnprocessableEntity);
        none.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_channels");
    }

    [Fact]
    public async Task Unknown_bus_is_404_and_teacher_is_403()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var payload = new { event_type = "departed", channels = new[] { "push" } };

        (await Client(app, tenantId, Policies.Principal).PostAsJsonAsync(
            $"/v1/transport/buses/{Guid.NewGuid()}/notify", payload)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client(app, tenantId, Policies.Teacher).PostAsJsonAsync(
            $"/v1/transport/buses/{Guid.NewGuid()}/notify", payload)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
