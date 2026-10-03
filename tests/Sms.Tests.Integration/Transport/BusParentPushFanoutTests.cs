using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Push;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusParentPushFanoutTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const double StopLat = 12.9000, StopLng = 77.6000;

    private sealed class SpyExpoPushSender : IExpoPushSender
    {
        public List<(IReadOnlyList<string> Tokens, string Title, string Body)> Calls { get; } = [];
        public bool Throw { get; set; }
        public Task SendAsync(IReadOnlyList<string> tokens, string title, string body,
            IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default)
        {
            Calls.Add((tokens, title, body));
            if (Throw) throw new InvalidOperationException("expo down");
            return Task.CompletedTask;
        }
    }

    private (WebApplicationFactory<Program> App, SpyExpoPushSender Push) App()
    {
        var push = new SpyExpoPushSender();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(s => s.AddSingleton<IExpoPushSender>(push));
        });
        return (app, push);
    }

    private static HttpClient Driver(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    // Seeds a bus on a one-stop route with one rider + parent at that stop, plus (optionally) a
    // registered device token for that parent. Returns the busNo, routeId and parentId.
    private async Task<(string BusNo, Guid RouteId, Guid ParentId)> Seed(Guid tenantId, bool withDevice)
    {
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var adm = $"ADM-{Guid.NewGuid():N}"[..14];

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\",\"RouteId\",\"RouteName\") VALUES (@Id,@T,@B,@R,'North')",
            new { Id = busId, T = tenantId, B = busNo, R = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\",\"TenantId\",\"Name\") VALUES (@R,@T,'North')",
            new { R = routeId, T = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"RouteStops\" (\"Id\",\"TenantId\",\"RouteId\",\"Name\",\"Seq\",\"Lat\",\"Lng\") VALUES (@Id,@T,@R,'Gate A',1,@La,@Ln)",
            new { Id = stopId, T = tenantId, R = routeId, La = StopLat, Ln = StopLng });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Students\" (\"Id\",\"TenantId\",\"AdmissionNo\",\"Name\",\"GuardianPhone\") VALUES (@Id,@T,@A,'Asha','9876543210')",
            new { Id = studentId, T = tenantId, A = adm });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\",\"TenantId\",\"StudentId\",\"BusId\",\"RouteId\",\"StopId\") VALUES (@Id,@T,@S,@B,@R,@Stop)",
            new { Id = Guid.NewGuid(), T = tenantId, S = studentId, B = busId, R = routeId, Stop = stopId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Users\" (\"Id\",\"TenantId\",\"StudentId\",\"IsPlatform\",\"Status\") VALUES (@Id,@T,@Adm,false,'active')",
            new { Id = parentId, T = tenantId, Adm = adm });
        if (withDevice)
            await conn.ExecuteAsync("INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@T,@U,@K,'ios')",
                new { T = tenantId, U = parentId, K = $"ExponentPushToken[{Guid.NewGuid():N}]" });
        return (busNo, routeId, parentId);
    }

    private static async Task<Guid> StartTrip(HttpClient driver, string busNo, Guid routeId)
    {
        var start = await driver.PostAsJsonAsync("/v1/staff/trips", new { direction = "pickup", bus_no = busNo, route_id = routeId });
        start.EnsureSuccessStatusCode();
        return (await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> Ping(HttpClient driver, Guid tripId) =>
        driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[] { new { lat = StopLat, lng = StopLng, speed_kmh = 20, heading = 10, at = DateTime.UtcNow } },
        });

    [Fact]
    public async Task A_registered_parent_gets_a_push_on_trip_start_and_on_approach()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, _) = await Seed(tenantId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId); // fires trip-started push
        push.Calls.Should().Contain(c => c.Title == "Bus started");

        (await Ping(driver, tripId)).EnsureSuccessStatusCode(); // fires approaching push
        push.Calls.Should().Contain(c => c.Title == "Bus near stop");
        push.Calls.Where(c => c.Title == "Bus near stop").Should().OnlyContain(c => c.Tokens.Count == 1);
    }

    [Fact]
    public async Task A_parent_with_no_registered_device_triggers_no_push()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, _) = await Seed(tenantId, withDevice: false);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).EnsureSuccessStatusCode();

        push.Calls.Should().BeEmpty("no device token means no push");
    }

    [Fact]
    public async Task A_failing_push_does_not_break_ingest_or_the_in_app_notice()
    {
        var (app, push) = App();
        push.Throw = true;
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, routeId, parentId) = await Seed(tenantId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var notices = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"Notifications\" WHERE \"UserId\" = @u AND \"Icon\" = 'bus'", new { u = parentId });
        notices.Should().BeGreaterThan(0, "the in-app notice must persist even when push throws");
    }
}
