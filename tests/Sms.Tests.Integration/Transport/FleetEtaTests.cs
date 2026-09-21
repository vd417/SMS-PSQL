using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Application.Services.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class FleetEtaTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task BuildAsync_carries_the_eta_to_the_next_stop()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'Route A')",
                new { Id = routeId, TenantId = tenantId });
            // Two stops ~1.11km apart (0.01 deg latitude) so a next-stop ETA is computable.
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""RouteStops"" (""Id"", ""TenantId"", ""RouteId"", ""Name"", ""Seq"", ""Lat"", ""Lng"") VALUES
                  (@S1, @TenantId, @RouteId, 'Stop 1', 1, 12.10, 77.10),
                  (@S2, @TenantId, @RouteId, 'Stop 2', 2, 12.11, 77.10)",
                new { S1 = Guid.NewGuid(), S2 = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\") VALUES (@Id, @TenantId, 'BUS-1', @RouteId)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""Trips"" (""Id"", ""TenantId"", ""BusId"", ""Direction"", ""Status"", ""StartedAt"")
                  VALUES (@Id, @TenantId, @BusId, 'pickup', 'live', now())",
                new { Id = tripId, TenantId = tenantId, BusId = busId });
            // Bus sits exactly at Stop 1, moving at 33.3 km/h -> ETA to Stop 2 (~1.11km) is ~2 minutes.
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""TripPings"" (""Id"", ""TenantId"", ""TripId"", ""Lat"", ""Lng"", ""SpeedKmh"", ""Heading"", ""At"")
                  VALUES (@Id, @TenantId, @TripId, 12.10, 77.10, 33.3, 0, now())",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        using var scope = app.Services.CreateScope();
        // isPlatform: true bypasses the TransportGps plan-feature gate, which a freshly
        // seeded test tenant (no plan/tier row) would otherwise fail, nulling out GPS fields.
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: true);
        var builder = scope.ServiceProvider.GetRequiredService<FleetSnapshotBuilder>();

        var rows = await builder.BuildAsync(default);

        var row = rows.Should().ContainSingle(r => r.BusId == busId).Subject;
        row.EtaMinutes.Should().Be(2);
    }
}
