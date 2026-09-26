using Dapper;
using Npgsql;

namespace Sms.Tests.Integration.Transport;

/// Seeds the bus-assignment rows POST /v1/staff/trips requires: the caller must be the bus's
/// assigned driver or conductor (Buses.DriverStaffId/ConductorStaffId -> Staff.UserId).
internal static class TripTestSeed
{
    public static async Task<Guid> AssignDriverAsync(
        string cs, Guid tenantId, string busNo, Guid driverUserId, Guid? conductorUserId = null)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });

        var driverStaffId = await EnsureStaffAsync(conn, tenantId, driverUserId, "Test Driver");
        Guid? conductorStaffId = conductorUserId is { } cu
            ? await EnsureStaffAsync(conn, tenantId, cu, "Test Conductor")
            : null;

        var busId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Buses\" WHERE \"TenantId\" = @tenantId AND \"BusNo\" = @busNo ORDER BY \"Id\" LIMIT 1",
            new { tenantId, busNo });
        if (busId is null)
        {
            busId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"DriverStaffId\", \"ConductorStaffId\") VALUES (@Id, @TenantId, @BusNo, @DriverStaffId, @ConductorStaffId)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo, DriverStaffId = driverStaffId, ConductorStaffId = conductorStaffId });
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE \"dbo\".\"Buses\" SET \"DriverStaffId\" = @DriverStaffId, \"ConductorStaffId\" = COALESCE(@ConductorStaffId, \"ConductorStaffId\") WHERE \"Id\" = @Id",
                new { Id = busId, DriverStaffId = driverStaffId, ConductorStaffId = conductorStaffId });
        }
        return busId.Value;
    }

    private static async Task<Guid> EnsureStaffAsync(NpgsqlConnection conn, Guid tenantId, Guid userId, string name)
    {
        // IX_Staff_UserId is unique — reuse a Staff row a test already created for this user.
        var existing = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Staff\" WHERE \"UserId\" = @userId", new { userId });
        if (existing is { } id) return id;
        var staffId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@Id, @TenantId, @Name, @UserId)",
            new { Id = staffId, TenantId = tenantId, Name = name, UserId = userId });
        return staffId;
    }
}
