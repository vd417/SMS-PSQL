using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class StudentTransportMappingTests(PostgresFixture fx)
{
    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task StudentTransport_Upsert_creates_pending_row_with_null_bus()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = Guid.NewGuid();
        var routeId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantId, A = "T-001", N = "Test Student" });
            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "Route Pending" });

            await conn.ExecuteAsync("dbo.StudentTransport_Upsert",
                new { TenantId = tenantId, StudentId = studentId, RouteId = routeId, StopId = (Guid?)null, FeeHeadId = (Guid?)null, BusId = (Guid?)null },
                commandType: System.Data.CommandType.StoredProcedure);
        });

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var row = await conn.QuerySingleAsync<(Guid? BusId, Guid RouteId)>(
            "SELECT BusId, RouteId FROM dbo.StudentBusAssignments WHERE StudentId = @studentId", new { studentId });

        row.BusId.Should().BeNull();
        row.RouteId.Should().Be(routeId);
    }
}
