using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Dapper;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TripStopEndpointsTests(PostgresFixture fx)
{
    private const string Key = "test-signing-key-at-least-32-bytes-long!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static string IssueToken(Guid userId, Guid tenantId, string role) =>
        new JwtTokenService(new JwtOptions { SigningKey = Key }, new SystemClock())
            .IssueAccess(userId, tenantId, [role], isPlatform: false);

    private async Task<(Guid tenantId, Guid tripId, Guid driverId, Guid stop1, Guid stop2)> SeedLiveTripWithTwoStops()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var stop1 = Guid.NewGuid();
        var stop2 = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo, DriverStaffId) VALUES (@Id, @TenantId, 'BUS-1', @DriverId)",
            new { Id = busId, TenantId = tenantId, DriverId = driverId });
        await conn.ExecuteAsync(
            @"INSERT INTO dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES
              (@S1, @TenantId, @RouteId, 'Stop A', 1, 12.1000, 77.1000),
              (@S2, @TenantId, @RouteId, 'Stop B', 2, 12.2000, 77.2000)",
            new { S1 = stop1, S2 = stop2, TenantId = tenantId, RouteId = routeId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.Trips (Id, TenantId, BusId, RouteId, DriverId, Direction, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @RouteId, @DriverId, 'pickup', 'live', SYSUTCDATETIME())",
            new { Id = tripId, TenantId = tenantId, BusId = busId, RouteId = routeId, DriverId = driverId });
        return (tenantId, tripId, driverId, stop1, stop2);
    }

    private static HttpClient AuthedClient(WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", IssueToken(userId, tenantId, role));
        return client;
    }

    [Fact]
    public async Task ConfirmArrival_out_of_order_stop_is_rejected()
    {
        var (tenantId, tripId, driverId, _, stop2) = await SeedLiveTripWithTwoStops();
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop2}/confirm-arrival", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConfirmArrival_then_Complete_advances_CurrentStopId_and_allows_the_next_stop()
    {
        var (tenantId, tripId, driverId, stop1, stop2) = await SeedLiveTripWithTwoStops();
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        await SeedPing(tenantId, tripId, lat: 12.1000, lng: 77.1000); // at Stop A
        var confirm1 = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/confirm-arrival", null);
        confirm1.IsSuccessStatusCode.Should().BeTrue();

        var completeBeforeConfirm2 = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop2}/complete", null);
        completeBeforeConfirm2.StatusCode.Should().Be(HttpStatusCode.Conflict, "stop2 was never confirmed as current");

        var complete1 = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/complete", null);
        complete1.IsSuccessStatusCode.Should().BeTrue();

        await SeedPing(tenantId, tripId, lat: 12.2000, lng: 77.2000); // bus has since moved to Stop B
        var confirm2 = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop2}/confirm-arrival", null);
        confirm2.IsSuccessStatusCode.Should().BeTrue();
    }

    private async Task SeedPing(Guid tenantId, Guid tripId, double lat, double lng)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) VALUES (NEWID(), @TenantId, @TripId, @Lat, @Lng, 0, 0, SYSUTCDATETIME())",
            new { TenantId = tenantId, TripId = tripId, Lat = lat, Lng = lng });
    }

    [Fact]
    public async Task SchoolArrived_sets_status_without_ending_the_trip()
    {
        var (tenantId, tripId, driverId, _, _) = await SeedLiveTripWithTwoStops();
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/school-arrived", null);
        res.IsSuccessStatusCode.Should().BeTrue();

        // Trip must still accept a subsequent action (e.g. End) — proving it wasn't closed.
        var endRes = await client.PostAsync($"/v1/staff/trips/{tripId}/end", null);
        endRes.IsSuccessStatusCode.Should().BeTrue();

        // The old proc matched WHERE Status = 'live' only, so /end on an 'arrived' trip was a
        // silent no-op that still returned 200 — assert the DB actually recorded Status='ended'
        // and EndedAt, not just that the HTTP call "succeeded".
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var row = await conn.QuerySingleAsync<(string Status, DateTime? EndedAt)>(
            "SELECT Status, EndedAt FROM dbo.Trips WHERE Id = @tripId", new { tripId });
        row.Status.Should().Be("ended");
        row.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SchoolArrived_then_End_then_a_drop_trip_can_start_on_the_same_bus()
    {
        var (tenantId, tripId, driverId, _, _) = await SeedLiveTripWithTwoStops();
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var arrivedRes = await client.PostAsync($"/v1/staff/trips/{tripId}/school-arrived", null);
        arrivedRes.IsSuccessStatusCode.Should().BeTrue();

        var endRes = await client.PostAsync($"/v1/staff/trips/{tripId}/end", null);
        endRes.IsSuccessStatusCode.Should().BeTrue();

        // Before the fix, Trip_Start's duplicate-active-trip guard (Status IN ('live','arrived'))
        // would still see this bus's pickup trip stuck at 'arrived' forever and reject the
        // return/drop leg — the headline scenario this whole feature exists to support.
        var startRes = await client.PostAsJsonAsync("/v1/staff/trips", new { RouteId = (Guid?)null, BusNo = "BUS-1", Direction = "drop" });
        startRes.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmArrival_with_no_ping_ever_sent_is_rejected_as_no_location()
    {
        // A driver who never sends a single GPS ping must not be able to bypass the
        // proximity check entirely by having no location to disprove — this used to
        // silently pass (skip, not reject) until the too_far check was tightened.
        var (tenantId, tripId, driverId, stop1, _) = await SeedLiveTripWithTwoStops();
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/confirm-arrival", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("no_location");
    }

    [Fact]
    public async Task ConfirmArrival_far_from_the_stop_is_rejected_as_too_far()
    {
        var (tenantId, tripId, driverId, stop1, _) = await SeedLiveTripWithTwoStops();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            // Stop A is at (12.10, 77.10); this ping is many kilometres away.
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) VALUES (NEWID(), @TenantId, @TripId, 20.0000, 90.0000, 0, 0, SYSUTCDATETIME())",
                new { TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/confirm-arrival", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("too_far");
    }

    [Fact]
    public async Task ConfirmArrival_reconfirming_the_current_stop_is_rejected_as_already_at_stop()
    {
        var (tenantId, tripId, driverId, stop1, _) = await SeedLiveTripWithTwoStops();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            // Stop A is at (12.10, 77.10); ping right on top of it so proximity passes.
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) VALUES (NEWID(), @TenantId, @TripId, 12.1000, 77.1000, 0, 0, SYSUTCDATETIME())",
                new { TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var confirm1 = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/confirm-arrival", null);
        confirm1.IsSuccessStatusCode.Should().BeTrue();

        var confirmAgain = await client.PostAsync($"/v1/staff/trips/{tripId}/stops/{stop1}/confirm-arrival", null);
        confirmAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await confirmAgain.Content.ReadAsStringAsync();
        body.Should().Contain("already_at_stop");
    }

    [Fact]
    public async Task SchoolArrived_persists_arrival_timestamp_and_gps_location()
    {
        var (tenantId, tripId, driverId, _, _) = await SeedLiveTripWithTwoStops();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) VALUES (NEWID(), @TenantId, @TripId, 12.3456, 77.6543, 0, 0, SYSUTCDATETIME())",
                new { TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/school-arrived", null);
        res.IsSuccessStatusCode.Should().BeTrue();

        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await check.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var row = await check.QuerySingleAsync<(DateTime? SchoolArrivedAt, double? SchoolArrivedLat, double? SchoolArrivedLng)>(
            "SELECT SchoolArrivedAt, SchoolArrivedLat, SchoolArrivedLng FROM dbo.Trips WHERE Id = @tripId", new { tripId });
        row.SchoolArrivedAt.Should().NotBeNull();
        row.SchoolArrivedLat.Should().Be(12.3456);
        row.SchoolArrivedLng.Should().Be(77.6543);
    }

    [Fact]
    public async Task SchoolArrived_on_a_drop_trip_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo, DriverStaffId) VALUES (@Id, @TenantId, 'BUS-1', @DriverId)",
                new { Id = busId, TenantId = tenantId, DriverId = driverId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Trips (Id, TenantId, BusId, DriverId, Direction, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @DriverId, 'drop', 'live', SYSUTCDATETIME())",
                new { Id = tripId, TenantId = tenantId, BusId = busId, DriverId = driverId });
        }
        await using var app = App();
        var client = AuthedClient(app, driverId, tenantId, Policies.Driver);

        var res = await client.PostAsync($"/v1/staff/trips/{tripId}/school-arrived", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
