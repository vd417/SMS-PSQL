using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusTeacherRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const double StopLat = 12.9, StopLng = 77.6;

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static BusRepository RepoFor(WebApplicationFactory<Program> app, Guid tenantId, out IServiceScope scope)
    {
        scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        return scope.ServiceProvider.GetRequiredService<BusRepository>();
    }

    private async Task<(Guid BusId, Guid RouteId, Guid StopId)> SeedBus(Guid tenantId)
    {
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\",\"RouteId\") VALUES (@b,@t,'KA-R9',@r)",
            new { b = busId, t = tenantId, r = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\",\"TenantId\",\"Name\") VALUES (@r,@t,'R9')",
            new { r = routeId, t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"RouteStops\" (\"Id\",\"TenantId\",\"RouteId\",\"Name\",\"Seq\",\"Lat\",\"Lng\") VALUES (@s,@t,@r,'Gate A',1,@la,@ln)",
            new { s = stopId, t = tenantId, r = routeId, la = StopLat, ln = StopLng });
        return (busId, routeId, stopId);
    }

    private static Task AddDuty(NpgsqlConnection conn, Guid tenantId, Guid busId, Guid teacherId) =>
        conn.ExecuteAsync("INSERT INTO \"dbo\".\"BusAssignments\" (\"TenantId\",\"TeacherUserId\",\"BusId\") VALUES (@t,@u,@b)",
            new { t = tenantId, u = teacherId, b = busId });

    private static Task AddTraveling(NpgsqlConnection conn, Guid tenantId, Guid busId, Guid teacherId, Guid? stopId) =>
        conn.ExecuteAsync("INSERT INTO \"dbo\".\"BusTravelingTeachers\" (\"TenantId\",\"BusId\",\"TeacherUserId\",\"StopId\") VALUES (@t,@b,@u,@s)",
            new { t = tenantId, b = busId, u = teacherId, s = stopId });

    [Fact]
    public async Task ListBusTeachers_returns_duty_and_traveling_each_once()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var (busId, _, _) = await SeedBus(tenantId);
        var dutyOnly = Guid.NewGuid();
        var travelOnly = Guid.NewGuid();
        var both = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await AddDuty(conn, tenantId, busId, dutyOnly);
            await AddTraveling(conn, tenantId, busId, travelOnly, null);
            await AddDuty(conn, tenantId, busId, both);
            await AddTraveling(conn, tenantId, busId, both, null);
        }

        var repo = RepoFor(app, tenantId, out var scope);
        using (scope)
        {
            var rows = await repo.ListBusTeachersAsync(busId);
            rows.Select(r => r.TeacherUserId).Should().BeEquivalentTo(new[] { dutyOnly, travelOnly, both });
            rows.Should().OnlyContain(r => r.BusNo == "KA-R9");
        }
    }

    [Fact]
    public async Task ListStopMappedTeachers_returns_only_traveling_teachers_with_a_stop()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var (busId, _, stopId) = await SeedBus(tenantId);
        var mapped = Guid.NewGuid();
        var unmapped = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await AddTraveling(conn, tenantId, busId, mapped, stopId);
            await AddTraveling(conn, tenantId, busId, unmapped, null);
        }

        var repo = RepoFor(app, tenantId, out var scope);
        using (scope)
        {
            var rows = await repo.ListStopMappedTeachersAsync(busId);
            rows.Should().ContainSingle().Which.TeacherUserId.Should().Be(mapped);
            var row = rows.Single();
            row.StopId.Should().Be(stopId);
            row.StopName.Should().Be("Gate A");
            row.StopLat.Should().Be(StopLat);
            row.StopLng.Should().Be(StopLng);
        }
    }

    [Fact]
    public async Task TryInsertTeacherAlert_dedupes_per_trip_teacher_kind()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var repo = RepoFor(app, tenantId, out var scope);
        using (scope)
        {
            (await repo.TryInsertTeacherAlertAsync(tenantId, tripId, teacherId, BusParentAlertRules.TripStarted))
                .Should().BeTrue("first insert is new");
            (await repo.TryInsertTeacherAlertAsync(tenantId, tripId, teacherId, BusParentAlertRules.TripStarted))
                .Should().BeFalse("same trip/teacher/kind is a duplicate");
            (await repo.TryInsertTeacherAlertAsync(tenantId, tripId, teacherId, BusParentAlertRules.TripEnded))
                .Should().BeTrue("a different kind is new");
        }
    }
}
