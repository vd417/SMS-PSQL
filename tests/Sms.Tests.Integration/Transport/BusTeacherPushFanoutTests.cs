using System.Net;
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
public class BusTeacherPushFanoutTests(PostgresFixture fx)
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

    private async Task<(string BusNo, Guid BusId, Guid RouteId, Guid StopId)> SeedBus(Guid tenantId)
    {
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\",\"RouteId\",\"RouteName\") VALUES (@Id,@T,@B,@R,'North')",
            new { Id = busId, T = tenantId, B = busNo, R = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\",\"TenantId\",\"Name\") VALUES (@R,@T,'North')",
            new { R = routeId, T = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"RouteStops\" (\"Id\",\"TenantId\",\"RouteId\",\"Name\",\"Seq\",\"Lat\",\"Lng\") VALUES (@Id,@T,@R,'Gate A',1,@La,@Ln)",
            new { Id = stopId, T = tenantId, R = routeId, La = StopLat, Ln = StopLng });
        return (busNo, busId, routeId, stopId);
    }

    /// Creates a teacher user and attaches them to the bus as duty and/or traveling (with an
    /// optional mapped stop), and optionally registers one device token for them.
    private async Task<Guid> SeedTeacher(
        Guid tenantId, Guid busId, bool duty, bool traveling, Guid? travelingStopId, bool withDevice)
    {
        var teacherId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Users\" (\"Id\",\"TenantId\",\"IsPlatform\",\"Status\") VALUES (@Id,@T,false,'active')",
            new { Id = teacherId, T = tenantId });
        if (duty)
            await conn.ExecuteAsync("INSERT INTO \"dbo\".\"BusAssignments\" (\"TenantId\",\"TeacherUserId\",\"BusId\") VALUES (@T,@U,@B)",
                new { T = tenantId, U = teacherId, B = busId });
        if (traveling)
            await conn.ExecuteAsync("INSERT INTO \"dbo\".\"BusTravelingTeachers\" (\"TenantId\",\"BusId\",\"TeacherUserId\",\"StopId\") VALUES (@T,@B,@U,@S)",
                new { T = tenantId, B = busId, U = teacherId, S = travelingStopId });
        if (withDevice)
            await conn.ExecuteAsync("INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@T,@U,@K,'ios')",
                new { T = tenantId, U = teacherId, K = $"ExponentPushToken[{Guid.NewGuid():N}]" });
        return teacherId;
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

    private static Task<HttpResponseMessage> EndTrip(HttpClient driver, Guid tripId) =>
        driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/end", new { });

    private async Task<int> NoticeCount(Guid tenantId, Guid userId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"Notifications\" WHERE \"UserId\" = @u AND \"Icon\" = 'bus'", new { u = userId });
    }

    [Fact]
    public async Task Duty_and_traveling_teachers_get_a_push_on_trip_start_and_end()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, busId, routeId, _) = await SeedBus(tenantId);
        await SeedTeacher(tenantId, busId, duty: true, traveling: false, travelingStopId: null, withDevice: true);
        await SeedTeacher(tenantId, busId, duty: false, traveling: true, travelingStopId: null, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        push.Calls.Count(c => c.Body.Contains("has started its trip")).Should().Be(2, "both teachers get a start push");

        (await EndTrip(driver, tripId)).EnsureSuccessStatusCode();
        push.Calls.Count(c => c.Body.Contains("has ended its trip")).Should().Be(2, "both teachers get an end push");
    }

    [Fact]
    public async Task A_stop_mapped_teacher_gets_an_approaching_push()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, busId, routeId, stopId) = await SeedBus(tenantId);
        await SeedTeacher(tenantId, busId, duty: false, traveling: true, travelingStopId: stopId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).EnsureSuccessStatusCode();

        push.Calls.Should().Contain(c => c.Body.Contains("is about 1 km from Gate A"));
    }

    [Fact]
    public async Task A_teacher_with_no_device_gets_an_in_app_notice_but_no_push()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, busId, routeId, _) = await SeedBus(tenantId);
        var teacherId = await SeedTeacher(tenantId, busId, duty: true, traveling: false, travelingStopId: null, withDevice: false);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        await StartTrip(driver, busNo, routeId);

        push.Calls.Should().BeEmpty("no device token means no push");
        (await NoticeCount(tenantId, teacherId)).Should().BeGreaterThan(0, "the in-app notice is created regardless");
    }

    [Fact]
    public async Task A_failing_push_does_not_break_start_ping_or_end()
    {
        var (app, push) = App();
        push.Throw = true;
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, busId, routeId, stopId) = await SeedBus(tenantId);
        await SeedTeacher(tenantId, busId, duty: false, traveling: true, travelingStopId: stopId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId); // start path survives a throwing sender
        (await Ping(driver, tripId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await EndTrip(driver, tripId)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Repeated_pings_send_the_approaching_push_only_once()
    {
        var (app, push) = App();
        await using var _ = app;
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busNo, busId, routeId, stopId) = await SeedBus(tenantId);
        await SeedTeacher(tenantId, busId, duty: false, traveling: true, travelingStopId: stopId, withDevice: true);
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        var driver = Driver(app, tenantId, driverId);

        var tripId = await StartTrip(driver, busNo, routeId);
        (await Ping(driver, tripId)).EnsureSuccessStatusCode();
        (await Ping(driver, tripId)).EnsureSuccessStatusCode();

        push.Calls.Count(c => c.Body.Contains("is about 1 km from Gate A")).Should().Be(1, "deduped per trip");
    }
}
