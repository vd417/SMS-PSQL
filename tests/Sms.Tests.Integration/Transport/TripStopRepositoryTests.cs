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
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
            new { Id = busId, TenantId = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Trips\" (\"Id\", \"TenantId\", \"BusId\", \"RouteId\", \"Direction\", \"Status\", \"StartedAt\") VALUES (@Id, @TenantId, @BusId, @RouteId, 'pickup', 'live', now())",
            new { Id = tripId, TenantId = tenantId, BusId = busId, RouteId = routeId });
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""RouteStops"" (""Id"", ""TenantId"", ""RouteId"", ""Name"", ""Seq"", ""Lat"", ""Lng"") VALUES
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
    public async Task ConfirmArrival_is_won_once_a_duplicate_confirm_claims_nothing()
    {
        // Race: driver and conductor both tap "Arrived" for the same stop. Both pass the service's
        // pre-checks (both read CurrentStopId == null) and reach the repo. The atomic claim must let
        // exactly one win (1 row) and report the other as a no-op (0 rows) so the caller can suppress
        // the duplicate fleet broadcast, rather than both silently succeeding.
        var (tenantId, _, tripId, stop1, _) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        var winner = await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);
        var duplicate = await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);

        winner.Should().Be(1);
        duplicate.Should().Be(0);
        (await repo.GetCurrentStopIdAsync(tripId, default)).Should().Be(stop1);
    }

    [Fact]
    public async Task CompleteStop_is_won_once_a_duplicate_depart_releases_nothing()
    {
        // Race: driver and conductor both tap "Depart" for the current stop. The atomic release must
        // let exactly one win (1 row) and report the other as a no-op (0 rows) — otherwise both
        // broadcast a completion for a stop that was only departed once.
        var (tenantId, _, tripId, stop1, _) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();
        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);

        var winner = await repo.CompleteStopAsync(tenantId, tripId, stop1, DateTime.UtcNow, default);
        var duplicate = await repo.CompleteStopAsync(tenantId, tripId, stop1, DateTime.UtcNow, default);

        winner.Should().Be(1);
        duplicate.Should().Be(0);
        (await repo.GetCurrentStopIdAsync(tripId, default)).Should().BeNull();
    }

    [Fact]
    public async Task EndAsync_StopsCovered_counts_stops_visited_not_stops_where_students_boarded()
    {
        // "Stops covered" is how many stops the bus actually reached, which is TripStopProgress
        // (arrival), not where a student happened to board. A trip can pass a stop with no boarders
        // and still have covered it; boarding at one stop must not undercount the two it visited.
        var (tenantId, _, tripId, stop1, stop2) = await Seed();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        // The bus visits both stops...
        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop1, 1, DateTime.UtcNow, DateTime.UtcNow, default);
        await repo.CompleteStopAsync(tenantId, tripId, stop1, DateTime.UtcNow, default);
        await repo.ConfirmStopArrivalAsync(tenantId, tripId, stop2, 2, DateTime.UtcNow, DateTime.UtcNow, default);
        await repo.CompleteStopAsync(tenantId, tripId, stop2, DateTime.UtcNow, default);
        // ...but a student boards at only one of them.
        await repo.UpsertBoardingAsync(tenantId, tripId, new BoardingRequest(Guid.NewGuid(), stop1, "boarded", DateTime.UtcNow), default);

        var summary = await repo.EndAsync(tenantId, tripId, default);

        summary.StopsCovered.Should().Be(2);
        summary.BoardedCount.Should().Be(1);
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
