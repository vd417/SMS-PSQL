using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TripStopRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private async Task<(Guid tenantId, Guid routeId, Guid tripId, Guid stop1, Guid stop2)> Seed()
    {
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var stop1 = Guid.NewGuid();
        var stop2 = Guid.NewGuid();
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.Trips (Id, TenantId, BusId, RouteId, Direction, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @RouteId, 'pickup', 'live', SYSUTCDATETIME())",
            new { Id = tripId, TenantId = tenantId, BusId = busId, RouteId = routeId });
        await conn.ExecuteAsync(
            @"INSERT INTO dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES
              (@S1, @TenantId, @RouteId, 'Stop A', 1, 12.1, 77.1),
              (@S2, @TenantId, @RouteId, 'Stop B', 2, 12.2, 77.2)",
            new { S1 = stop1, S2 = stop2, TenantId = tenantId, RouteId = routeId });
        return (tenantId, routeId, tripId, stop1, stop2);
    }

    [Fact]
    public async Task GetNextIncompleteStopAsync_returns_the_first_stop_when_none_completed()
    {
        var (tenantId, routeId, tripId, stop1, _) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var next = await repo.GetNextIncompleteStopAsync(tripId, routeId, default);
        next.Should().NotBeNull();
        next!.Id.Should().Be(stop1);
    }

    [Fact]
    public async Task ConfirmArrival_sets_CurrentStopId_and_Complete_advances_to_the_next_stop()
    {
        var (tenantId, routeId, tripId, stop1, stop2) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);
        (await repo.GetCurrentStopIdAsync(tripId, default)).Should().Be(stop1);

        // GetNextIncompleteStopAsync considers a stop "incomplete" until DepartedAt is set, so
        // while stop1 is confirmed-but-undeparted it is still the "next incomplete" stop, not
        // stop2. This is intentional: excluding the current stop is the caller's job (Task 4
        // guards with `if (currentStopId is null && ...)` before calling this method).
        (await repo.GetNextIncompleteStopAsync(tripId, routeId, default))!.Id.Should().Be(stop1);

        await repo.CompleteStopAsync(tenantId, tripId, stop1, DateTime.UtcNow, default);
        (await repo.GetCurrentStopIdAsync(tripId, default)).Should().BeNull();

        var next = await repo.GetNextIncompleteStopAsync(tripId, routeId, default);
        next!.Id.Should().Be(stop2);
    }

    [Fact]
    public async Task GetNextIncompleteStopAsync_returns_null_when_all_stops_completed()
    {
        var (tenantId, routeId, tripId, stop1, stop2) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);
        await repo.CompleteStopAsync(tenantId, tripId, stop1, DateTime.UtcNow, default);
        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop2, 2, DateTime.UtcNow, DateTime.UtcNow, default);
        await repo.CompleteStopAsync(tenantId, tripId, stop2, DateTime.UtcNow, default);

        (await repo.GetNextIncompleteStopAsync(tripId, routeId, default)).Should().BeNull();
    }
}
