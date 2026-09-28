using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.DevSeed;
using Xunit;

namespace Sms.Tests.Integration.DevSeed;

/// NEW-4: the app's principal teacher-reassignment DELETEs and re-INSERTs dbo.BusAssignments
/// with a fresh Id, so after an e2e run the seed's canonical row is gone but a row with the same
/// (TenantId, TeacherUserId, BusId) — the table's real identity — still satisfies it. The old
/// Id-only ExistsAsync couldn't tell that from a genuine conflict and threw "shadowed", poisoning
/// the seed for every run after. SeedRunner must check the table's natural key instead of Id when
/// one is registered for that table.
[Collection("sql")]
public class SeedRunnerNaturalKeyTests(PostgresFixture fx)
{
    private static async Task SetPlatformAsync(NpgsqlConnection conn) =>
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false);");

    [Fact]
    public async Task Poisoned_row_with_same_natural_key_but_different_id_is_accepted_without_duplicating()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var seedId = Guid.NewGuid();

        var seedRow = new SeedRow("BusAssignments", new Dictionary<string, object>
        {
            ["Id"] = seedId, ["TenantId"] = tenantId, ["TeacherUserId"] = teacherUserId, ["BusId"] = busId,
        });

        // First run: seed lands normally.
        var first = await SeedRunner.RunAsync(fx.ConnectionString, [seedRow]);
        first.PerTable["BusAssignments"].Inserted.Should().Be(1);

        // Simulate the app's teacher-reassignment: DELETE the seed row and re-INSERT an
        // equivalent row (same natural key) under a fresh, random Id.
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await SetPlatformAsync(conn);
            await conn.ExecuteAsync(
                "DELETE FROM \"dbo\".\"BusAssignments\" WHERE \"Id\" = @seedId", new { seedId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusAssignments\" (\"Id\", \"TenantId\", \"TeacherUserId\", \"BusId\") " +
                "VALUES (@id, @tenantId, @teacherUserId, @busId)",
                new { id = Guid.NewGuid(), tenantId, teacherUserId, busId });
        }

        // Re-running the seed must succeed: the natural key is satisfied even though the Id
        // differs, and the app's replacement row must not be duplicated.
        var second = await SeedRunner.RunAsync(fx.ConnectionString, [seedRow]);
        second.PerTable["BusAssignments"].Skipped.Should().Be(1);
        second.PerTable["BusAssignments"].Inserted.Should().Be(0);

        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await SetPlatformAsync(check);
        var count = await check.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM \"dbo\".\"BusAssignments\" WHERE \"TenantId\" = @tenantId AND \"TeacherUserId\" = @teacherUserId AND \"BusId\" = @busId",
            new { tenantId, teacherUserId, busId });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Genuinely_conflicting_row_with_a_different_bus_still_throws_and_commits_nothing()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var conflictingBusId = Guid.NewGuid();
        var seedId = Guid.NewGuid();

        var seedRow = new SeedRow("BusAssignments", new Dictionary<string, object>
        {
            ["Id"] = seedId, ["TenantId"] = tenantId, ["TeacherUserId"] = teacherUserId, ["BusId"] = busId,
        });

        // A non-seed row already holds the unique (TenantId, TeacherUserId) slot, but for a
        // DIFFERENT bus — a genuine, real conflict, not a poisoned-but-equivalent row.
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await SetPlatformAsync(conn);
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusAssignments\" (\"Id\", \"TenantId\", \"TeacherUserId\", \"BusId\") " +
                "VALUES (@id, @tenantId, @teacherUserId, @conflictingBusId)",
                new { id = Guid.NewGuid(), tenantId, teacherUserId, conflictingBusId });
        }

        var act = () => SeedRunner.RunAsync(fx.ConnectionString, [seedRow]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shadowed*");

        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await SetPlatformAsync(check);
        var seedRowExists = await check.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM \"dbo\".\"BusAssignments\" WHERE \"Id\" = @seedId", new { seedId });
        seedRowExists.Should().Be(0);
    }
}
