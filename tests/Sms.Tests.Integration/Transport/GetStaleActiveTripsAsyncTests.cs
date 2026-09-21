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
}
