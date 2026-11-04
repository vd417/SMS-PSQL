using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

/// The automatic parent "bus near stop" alert, fired from the live GPS ping pipeline, must only
/// fire off a trustworthy fix (fresh + accurate), only for the trip's next incomplete stop, and
/// only from the real driver/conductor ping path — never from the operator/admin backfill path.
[Collection("sql")]
public class BusParentApproachAlertTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    // A stop and a ping close enough to be well within the 1 km approach radius.
    private const double StopLat = 12.9000, StopLng = 77.6000;
    private const double FarLat = 13.5000, FarLng = 78.2000; // ~80 km away — never in radius.

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<NpgsqlConnection> Open(string cs, Guid tenantId)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    // Count only the "bus near stop" approach alert, not the separate "Bus started" trip notice.
    private async Task<int> ApproachCount(Guid tenantId, Guid userId)
    {
        await using var conn = await Open(fx.ConnectionString, tenantId);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"Notifications\" WHERE \"UserId\" = @userId AND \"Icon\" = 'bus' AND \"Title\" = 'Bus near stop'",
            new { userId });
    }

    /// A bus (BusNo) on a two-stop route; a rider + parent login at each stop. Stop A is seq 1 (the
    /// trip's next incomplete stop), stop B is seq 2. Caller places the stops' coordinates.
    private async Task<(Guid busId, Guid routeId, Guid stopA, Guid stopB, Guid parentA, Guid parentB)> SeedBusRoute(
        Guid tenantId, string busNo, (double Lat, double Lng) a, (double Lat, double Lng) b)
    {
        await using var conn = await Open(fx.ConnectionString, tenantId);
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopA = Guid.NewGuid();
        var stopB = Guid.NewGuid();
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();

        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\", \"RouteName\") VALUES (@Id, @TenantId, @BusNo, @RouteId, 'North')",
            new { Id = busId, TenantId = tenantId, BusNo = busNo, RouteId = routeId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'North')",
            new { Id = routeId, TenantId = tenantId });
        foreach (var (stop, seq, name, coord) in new[] { (stopA, 1, "Gate A", a), (stopB, 2, "Gate B", b) })
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"RouteStops\" (\"Id\", \"TenantId\", \"RouteId\", \"Name\", \"Seq\", \"Lat\", \"Lng\") VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, @Lat, @Lng)",
                new { Id = stop, TenantId = tenantId, RouteId = routeId, Name = name, Seq = seq, coord.Lat, coord.Lng });
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
        return (busId, routeId, stopA, stopB, parentA, parentB);
    }

    private static object Ping(double lat, double lng, DateTime at, double? accuracy = null) => new
    {
        pings = new[] { new { lat, lng, speed_kmh = 20, heading = 10, at, accuracy } },
    };

    [Fact]
    public async Task Driver_ping_near_the_next_stop_notifies_only_that_stops_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        // Both stops co-located so the distance filter passes for both riders — only the
        // target-stop filter should keep rider B (stop 2) out.
        var (busId, routeId, _, _, parentA, parentB) =
            await SeedBusRoute(tenantId, busNo, (StopLat, StopLng), (StopLat, StopLng));
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Client(app, tenantId, driverId, "driver");

        var start = await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo, route_id = routeId });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var tripId = (await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        (await driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings",
            Ping(StopLat, StopLng, DateTime.UtcNow))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ApproachCount(tenantId, parentA)).Should().Be(1, "rider at the next incomplete stop is notified");
        (await ApproachCount(tenantId, parentB)).Should().Be(0, "a rider at a later stop is not the current target");
    }

    [Fact]
    public async Task A_stale_fix_does_not_notify()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (_, routeId, _, _, parentA, _) =
            await SeedBusRoute(tenantId, busNo, (StopLat, StopLng), (FarLat, FarLng));
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Client(app, tenantId, driverId, "driver");

        var tripId = (await (await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo, route_id = routeId }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        (await driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings",
            Ping(StopLat, StopLng, DateTime.UtcNow.AddSeconds(-120)))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ApproachCount(tenantId, parentA)).Should().Be(0, "a stale position must not raise a fresh 1 km alert");
    }

    [Fact]
    public async Task An_inaccurate_fix_does_not_notify()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (_, routeId, _, _, parentA, _) =
            await SeedBusRoute(tenantId, busNo, (StopLat, StopLng), (FarLat, FarLng));
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Client(app, tenantId, driverId, "driver");

        var tripId = (await (await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo, route_id = routeId }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        (await driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings",
            Ping(StopLat, StopLng, DateTime.UtcNow, accuracy: 300))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ApproachCount(tenantId, parentA)).Should().Be(0, "an unreliable fix (>200 m) must not raise an alert");
    }

    [Fact]
    public async Task The_operator_backfill_ping_path_does_not_notify_parents()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, _, _, _, parentA, _) =
            await SeedBusRoute(tenantId, busNo, (StopLat, StopLng), (FarLat, FarLng));
        var admin = Client(app, tenantId, principalId, Policies.Principal);

        var start = await admin.PostAsJsonAsync($"/v1/transport/buses/{busId}/trip/start", new { direction = "pickup" });
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var tripId = (await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("data").GetProperty("id").GetGuid();

        (await admin.PostAsJsonAsync($"/v1/transport/buses/{busId}/trip/pings",
            Ping(StopLat, StopLng, DateTime.UtcNow))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ApproachCount(tenantId, parentA)).Should().Be(0, "operator/admin backfill pings must not raise parent alerts");
    }
}
