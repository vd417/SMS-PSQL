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
public class FleetHeadingTests(PostgresFixture fx)
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
    public async Task FleetAsync_carries_the_latest_pings_heading()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
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
                  VALUES (@Id, @TenantId, @TripId, 12.1, 77.1, 25, 214.5, SYSUTCDATETIME())",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId });
        }
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        var rows = await repo.FleetAsync(default);

        var row = rows.Should().ContainSingle(r => r.BusId == busId).Subject;
        row.Heading.Should().Be(214.5);
    }
}
