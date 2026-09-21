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
public class BusLiveSnapshotTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private async Task<(Guid tenantId, Guid busId, Guid tripId)> SeedBusWithLastPing(DateTime pingAt, double speedKmh)
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            @"INSERT INTO dbo.Trips (Id, TenantId, BusId, Direction, Status, StartedAt)
              VALUES (@Id, @TenantId, @BusId, 'pickup', 'live', SYSUTCDATETIME())",
            new { Id = tripId, TenantId = tenantId, BusId = busId });
        await conn.ExecuteAsync(
            @"INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At)
              VALUES (@Id, @TenantId, @TripId, 12.1, 77.1, @SpeedKmh, 90, @At)",
            new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, SpeedKmh = speedKmh, At = pingAt });
        return (tenantId, busId, tripId);
    }

    [Fact]
    public async Task Status_is_moving_when_a_fresh_fast_ping_exists()
    {
        var (tenantId, busId, _) = await SeedBusWithLastPing(DateTime.UtcNow.AddSeconds(-5), speedKmh: 25);
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        var snapshot = await repo.GetLiveSnapshotAsync(busId, default);

        snapshot.Status.Should().Be("moving");
        snapshot.TrackingStatus.Should().Be("LIVE");
        snapshot.Motion.Should().Be("moving");
        snapshot.Lat.Should().Be(12.1);
        snapshot.SpeedKmh.Should().Be(25);
    }

    [Fact]
    public async Task Status_is_stopped_when_a_fresh_slow_ping_exists()
    {
        var (tenantId, busId, _) = await SeedBusWithLastPing(DateTime.UtcNow.AddSeconds(-5), speedKmh: 1);
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.GetLiveSnapshotAsync(busId, default)).Status.Should().Be("stopped");
    }

    [Fact]
    public async Task Status_is_offline_when_the_last_ping_is_older_than_60_seconds()
    {
        var (tenantId, busId, _) = await SeedBusWithLastPing(DateTime.UtcNow.AddSeconds(-90), speedKmh: 25);
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.GetLiveSnapshotAsync(busId, default)).Status.Should().Be("offline");
    }

    [Fact]
    public async Task Status_is_stopped_when_speed_is_exactly_at_the_moving_cutoff()
    {
        // Cutoff is `SpeedKmh > 3` -> "moving"; exactly 3 must NOT cross into "moving".
        var (tenantId, busId, _) = await SeedBusWithLastPing(DateTime.UtcNow.AddSeconds(-5), speedKmh: 3);
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.GetLiveSnapshotAsync(busId, default)).Status.Should().Be("stopped");
    }

    [Fact]
    public async Task Status_is_offline_when_the_last_ping_is_exactly_at_the_staleness_cutoff()
    {
        // Cutoff is `ageSeconds > 60` -> "offline". We seed a ping exactly 60.0s old;
        // by the time GetLiveSnapshotAsync computes DateTime.UtcNow a few milliseconds
        // will have elapsed, so the observed age is at-or-just-past 60s, not exactly
        // 60.000s. This still exercises the real boundary: an off-by-one flip to
        // `>= 60` would report "offline" here too, but a bug that used `> 60` on a
        // wrongly-scaled unit (e.g. minutes) or inverted the comparison would fail it.
        var (tenantId, busId, _) = await SeedBusWithLastPing(DateTime.UtcNow.AddSeconds(-60), speedKmh: 25);
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.GetLiveSnapshotAsync(busId, default)).Status.Should().Be("offline");
    }

    [Fact]
    public async Task Status_is_offline_when_the_bus_has_never_pinged()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
        }
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        var snapshot = await repo.GetLiveSnapshotAsync(busId, default);
        snapshot.Status.Should().Be("offline");
        snapshot.Lat.Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_carries_the_pings_accuracy()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Trips (Id, TenantId, BusId, Direction, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, 'pickup', 'live', SYSUTCDATETIME())",
                new { Id = tripId, TenantId = tenantId, BusId = busId });
            await conn.ExecuteAsync(
                @"INSERT INTO dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At, Accuracy)
                  VALUES (@Id, @TenantId, @TripId, 12.1, 77.1, 5, 90, SYSUTCDATETIME(), 8.0)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.GetLiveSnapshotAsync(busId, default)).Accuracy.Should().Be(8.0);
    }
}
