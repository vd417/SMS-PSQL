using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusTeacherAlertsSchemaTests(PostgresFixture fx)
{
    private static async Task<NpgsqlConnection> Open(string cs, Guid tenantId)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    [Fact]
    public async Task BusTravelingTeachers_has_a_nullable_StopId()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        await using var conn = await Open(fx.ConnectionString, tenantId);
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\") VALUES (@b,@t,'KA-T1')",
            new { b = busId, t = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"BusTravelingTeachers\" (\"TenantId\",\"BusId\",\"TeacherUserId\",\"StopId\") VALUES (@t,@b,@u,@s)",
            new { t = tenantId, b = busId, u = teacherId, s = stopId });
        var got = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT \"StopId\" FROM \"dbo\".\"BusTravelingTeachers\" WHERE \"TeacherUserId\"=@u", new { u = teacherId });
        got.Should().Be(stopId);
    }

    [Fact]
    public async Task BusTeacherAlerts_rows_are_tenant_isolated()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        await using (var a = await Open(fx.ConnectionString, tenantA))
            await a.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusTeacherAlerts\" (\"TenantId\",\"TripId\",\"TeacherUserId\",\"Kind\") VALUES (@t,@trip,@u,'trip_started')",
                new { t = tenantA, trip = tripId, u = teacherId });

        await using var b = await Open(fx.ConnectionString, tenantB);
        var visible = await b.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"BusTeacherAlerts\" WHERE \"TripId\"=@trip", new { trip = tripId });
        visible.Should().Be(0, "tenant B must not see tenant A's teacher-alert rows");
    }

    [Fact]
    public async Task BusTeacherAlerts_is_unique_per_trip_teacher_kind()
    {
        var tenantId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        await using var conn = await Open(fx.ConnectionString, tenantId);
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"BusTeacherAlerts\" (\"TenantId\",\"TripId\",\"TeacherUserId\",\"Kind\") VALUES (@t,@trip,@u,'trip_started')",
            new { t = tenantId, trip = tripId, u = teacherId });

        var act = () => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"BusTeacherAlerts\" (\"TenantId\",\"TripId\",\"TeacherUserId\",\"Kind\") VALUES (@t,@trip,@u,'trip_started')",
            new { t = tenantId, trip = tripId, u = teacherId });

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }
}
