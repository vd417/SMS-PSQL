using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class GetStaleActiveTripsAsyncTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private async Task<Guid> SeedLiveTrip(Guid tenantId, DateTime? driverLastPingAt, DateTime? conductorLastPingAt)
    {
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""Trips"" (""Id"", ""TenantId"", ""BusId"", ""Direction"", ""Status"", ""StartedAt"", ""DriverLastPingAt"", ""ConductorLastPingAt"")
              VALUES (@Id, @TenantId, @BusId, 'pickup', 'live', now(), @DriverLastPingAt, @ConductorLastPingAt)",
            new { Id = tripId, TenantId = tenantId, BusId = busId, DriverLastPingAt = driverLastPingAt, ConductorLastPingAt = conductorLastPingAt });
        return tripId;
    }

    [Fact]
    public async Task Trips_with_no_ping_within_60_seconds_are_returned_as_stale()
    {
        var tenantId = Guid.NewGuid();
        var staleTripId = await SeedLiveTrip(tenantId, DateTime.UtcNow.AddSeconds(-90), null);
        var freshTripId = await SeedLiveTrip(tenantId, DateTime.UtcNow.AddSeconds(-5), null);
        var neverPingedTripId = await SeedLiveTrip(tenantId, null, null);

        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, isPlatform: true);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var stale = await repo.GetStaleActiveTripsAsync(TimeSpan.FromSeconds(60), default);
        var staleIds = stale.Select(s => s.TripId).ToHashSet();

        staleIds.Should().Contain(staleTripId);
        staleIds.Should().Contain(neverPingedTripId);
        staleIds.Should().NotContain(freshTripId);
    }

    [Fact]
    public async Task Uses_the_more_recent_of_driver_or_conductor_ping()
    {
        var tenantId = Guid.NewGuid();
        // Driver went silent, but conductor pinged 5s ago — the trip is NOT stale.
        var tripId = await SeedLiveTrip(tenantId, DateTime.UtcNow.AddSeconds(-90), DateTime.UtcNow.AddSeconds(-5));

        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, isPlatform: true);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var stale = await repo.GetStaleActiveTripsAsync(TimeSpan.FromSeconds(60), default);
        stale.Select(s => s.TripId).Should().NotContain(tripId);
    }

    private async Task<Guid> SeedTrip(Guid tenantId, string status, DateTime startedAt, DateTime? driverLastPingAt)
    {
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""Trips"" (""Id"", ""TenantId"", ""BusId"", ""Direction"", ""Status"", ""StartedAt"", ""DriverLastPingAt"")
              VALUES (@Id, @TenantId, @BusId, 'pickup', @Status, @StartedAt, @DriverLastPingAt)",
            new { Id = tripId, TenantId = tenantId, BusId = busId, Status = status, StartedAt = startedAt, DriverLastPingAt = driverLastPingAt });
        return tripId;
    }

    [Fact]
    public async Task AutoEnd_includes_long_silent_live_and_arrived_trips_but_not_recent_ones()
    {
        var tenantId = Guid.NewGuid();
        var autoEndAfter = TimeSpan.FromMinutes(30);
        var longAgo = DateTime.UtcNow.AddMinutes(-45);

        // Abandoned: started 45m ago, last ping 45m ago -> past the 30m threshold.
        var abandonedLive = await SeedTrip(tenantId, "live", longAgo, longAgo);
        // Abandoned 'arrived' pickup (ignored by the offline-stale query, but it still blocks the bus).
        var abandonedArrived = await SeedTrip(tenantId, "arrived", longAgo, longAgo);
        // Still live and pinging recently -> keep.
        var activeLive = await SeedTrip(tenantId, "live", longAgo, DateTime.UtcNow.AddSeconds(-5));
        // Just started, no ping yet -> must be measured from StartedAt, NOT auto-ended.
        var freshNeverPinged = await SeedTrip(tenantId, "live", DateTime.UtcNow.AddSeconds(-10), null);

        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, isPlatform: true);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var ids = (await repo.GetAutoEndCandidatesAsync(autoEndAfter, default)).Select(s => s.TripId).ToHashSet();

        ids.Should().Contain(abandonedLive);
        ids.Should().Contain(abandonedArrived);
        ids.Should().NotContain(activeLive);
        ids.Should().NotContain(freshNeverPinged);
    }

    private async Task<(Guid tripId, Guid busId, Guid routeId)> SeedTripWithRoute(
        Guid tenantId, string direction, string status, DateTime lastActivity, int stopCount)
    {
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        for (var i = 1; i <= stopCount; i++)
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""RouteStops"" (""Id"", ""TenantId"", ""RouteId"", ""Name"", ""Seq"", ""Lat"", ""Lng"")
                  VALUES (gen_random_uuid(), @TenantId, @RouteId, @Name, @Seq, 12.0, 77.0)",
                new { TenantId = tenantId, RouteId = routeId, Name = $"Stop {i}", Seq = i });
        var schoolArrivedAt = status == "arrived" ? (DateTime?)lastActivity : null;
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""Trips"" (""Id"", ""TenantId"", ""BusId"", ""RouteId"", ""Direction"", ""Status"", ""StartedAt"", ""DriverLastPingAt"", ""SchoolArrivedAt"")
              VALUES (@Id, @TenantId, @BusId, @RouteId, @Direction, @Status, @StartedAt, @LastPing, @SchoolArrivedAt)",
            new { Id = tripId, TenantId = tenantId, BusId = busId, RouteId = routeId, Direction = direction, Status = status,
                  StartedAt = lastActivity, LastPing = lastActivity, SchoolArrivedAt = schoolArrivedAt });
        return (tripId, busId, routeId);
    }

    private async Task CompleteAllStops(Guid tenantId, Guid tripId, Guid routeId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""TripStopProgress"" (""Id"", ""TenantId"", ""TripId"", ""StopId"", ""Seq"", ""ArrivedAt"", ""ConfirmedAt"", ""DepartedAt"")
              SELECT gen_random_uuid(), @TenantId, @TripId, rs.""Id"", rs.""Seq"", now(), now(), now()
              FROM ""dbo"".""RouteStops"" rs WHERE rs.""RouteId"" = @RouteId",
            new { TenantId = tenantId, TripId = tripId, RouteId = routeId });
    }

    [Fact]
    public async Task CompletedAutoEnd_ends_arrived_pickup_and_fully_covered_drop_after_grace_but_not_in_progress()
    {
        var tenantId = Guid.NewGuid();
        var grace = TimeSpan.FromMinutes(5);
        var idle = DateTime.UtcNow.AddMinutes(-10); // past the 5m grace
        var recent = DateTime.UtcNow.AddSeconds(-30); // within grace

        // Pickup that reached school 10m ago -> end.
        var (arrivedPickup, _, _) = await SeedTripWithRoute(tenantId, "pickup", "arrived", idle, 2);
        // Pickup that just arrived 30s ago -> keep (still inside grace).
        var (justArrivedPickup, _, _) = await SeedTripWithRoute(tenantId, "pickup", "arrived", recent, 2);
        // Drop with every stop covered, idle 10m -> end.
        var (coveredDrop, _, dropRouteId) = await SeedTripWithRoute(tenantId, "drop", "live", idle, 2);
        await CompleteAllStops(tenantId, coveredDrop, dropRouteId);
        // Drop still in progress (no stops completed) -> keep.
        var (inProgressDrop, _, _) = await SeedTripWithRoute(tenantId, "drop", "live", idle, 2);

        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, isPlatform: true);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var ids = (await repo.GetCompletedTripsToAutoEndAsync(grace, default)).Select(s => s.TripId).ToHashSet();

        ids.Should().Contain(arrivedPickup);
        ids.Should().Contain(coveredDrop);
        ids.Should().NotContain(justArrivedPickup);
        ids.Should().NotContain(inProgressDrop);
    }
}
