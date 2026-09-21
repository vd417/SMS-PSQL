using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.Migrations;
using Xunit;

namespace Sms.Tests.Integration.Migrations;

/// <summary>
/// Proves M0085's ACTUAL Up() (not a hand-copied SQL snippet) really backfills Teachers against
/// pre-existing cross-tenant data, and that its own elevated SESSION_CONTEXT is genuinely in
/// effect for every one of its RLS-touching statements.
///
/// PostgresFixture applies every migration (including M0085) once at fixture InitializeAsync,
/// before any test gets a chance to insert data -- so there is no "before" state left for a test
/// running against that shared fixture to seed. This test manages its own throwaway database
/// instead: migrate up to M0084 (schema only, no backfill yet), insert data the way a real,
/// non-elevated app connection would (one SESSION_CONTEXT per tenant, never IsPlatform), then run
/// the remaining migrations (M0085) and assert on the resulting rows. This is the mechanism the
/// code review asked for to close the "SESSION_CONTEXT persistence is unverified" gap: if the
/// elevation bundled into each Execute.Sql statement in M0085 didn't actually take effect, this
/// test's linked/backfilled assertions would fail with the rows still unlinked.
/// </summary>
public sealed class M0085_EndToEnd_MigrationTests : IAsyncLifetime
{
    private readonly string? _overrideCs = Environment.GetEnvironmentVariable("SMS_TEST_SQL_CONNECTION");
    private readonly string _server = Environment.GetEnvironmentVariable("SMS_TEST_SQL_SERVER") ?? "DESKTOP-TJL4SG6";
    private readonly string _dbName = "Sms_Test_M0085_" + Guid.NewGuid().ToString("N");
    private string _connectionString = "";

    // NOTE: this test exercises the retired FluentMigrator/SQL-Server migration runner
    // (Sms.Migrations.MigrationRunner, .AddSqlServer()) and cannot pass against Postgres as-is —
    // left compiling only (type names fixed) pending the Migrations-module decision (see
    // backend-api-postgresql-conversion-prompt.md section 6), not attempted here.
    private string MasterCs =>
        !string.IsNullOrEmpty(_overrideCs)
            ? new NpgsqlConnectionStringBuilder(_overrideCs) { Database = "postgres" }.ConnectionString
            : $"Server={_server};Database=master;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

    private string DbCs(string db) =>
        !string.IsNullOrEmpty(_overrideCs)
            ? new NpgsqlConnectionStringBuilder(_overrideCs) { Database = db }.ConnectionString
            : $"Server={_server};Database={db};Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";

    public async Task InitializeAsync()
    {
        await using (var master = new NpgsqlConnection(MasterCs))
        {
            await master.OpenAsync();
            await master.ExecuteAsync($"CREATE DATABASE [{_dbName}];");
        }
        _connectionString = DbCs(_dbName);
        // Schema only -- Teachers/Staff.UserId columns exist (M0084), but the M0085 backfill has
        // NOT run yet, so the data inserted by the test below is genuinely "pre-existing" from
        // the backfill migration's point of view, same as it would be in a real production DB.
        MigrationRunner.RunTo(_connectionString, 84);
    }

    public async Task DisposeAsync()
    {
        await using var master = new NpgsqlConnection(MasterCs);
        await master.OpenAsync();
        await master.ExecuteAsync(
            $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];");
    }

    [Fact]
    public async Task Real_migration_backfills_clean_matches_and_reports_ambiguous_and_no_match_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Insert as a normal, non-elevated app connection would: one SESSION_CONTEXT per tenant,
        // IsPlatform always 0. This is the pre-existing cross-tenant data the migration must see
        // once IT elevates itself -- exactly the scenario code review flagged as unverified.
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @v::text, false)", new { v = tenantA });
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '0', false)");
        var userA = Guid.NewGuid();
        var teacherA = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Email) VALUES (@userA, @tenantA, 'clean@x.com')",
            new { userA, tenantA });
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Email) VALUES (@teacherA, @tenantA, 'Clean Teacher', 'clean@x.com')",
            new { teacherA, tenantA });

        // Tenant B: a Users row with the SAME email as tenant A's teacher must NOT be able to
        // link across tenants (defence-in-depth check that scoping survived the restructure).
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @v::text, false)", new { v = tenantB });
        var userBSameEmail = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Email) VALUES (@userBSameEmail, @tenantB, 'clean@x.com')",
            new { userBSameEmail, tenantB });

        // Tenant B: ambiguous teacher -- matches two different Users rows (one by phone, one by email).
        var phone = "+19999999999";
        var email = "ambiguous@x.com";
        var teacherAmbig = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Phone) VALUES (@id, @tenantB, @phone)",
            new { id = Guid.NewGuid(), tenantB, phone });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Email) VALUES (@id, @tenantB, @email)",
            new { id = Guid.NewGuid(), tenantB, email });
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Phone, Email) VALUES (@teacherAmbig, @tenantB, 'Ambiguous Teacher', @phone, @email)",
            new { teacherAmbig, tenantB, phone, email });

        // Tenant B: no-match teacher.
        var teacherNoMatch = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, Email) VALUES (@teacherNoMatch, @tenantB, 'Lonely Teacher', 'nobody@x.com')",
            new { teacherNoMatch, tenantB });

        // Run the ACTUAL M0085 migration now, against this pre-existing data, exactly as it
        // would run against a real production database.
        MigrationRunner.Run(_connectionString);

        // Assertions below read across both tenants (teacherA in tenantA, teacherAmbig/teacherNoMatch
        // in tenantB), so elevate this session's own SESSION_CONTEXT for the read-back queries.
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");

        // -- Clean match: linked and Users.Name copied. This is the crux of issue #1: it only
        // passes if the elevated SESSION_CONTEXT genuinely took effect for the migration's
        // Teachers UPDATE, since this session's own context is tenantA / IsPlatform=0. --
        var linkedUserId = await conn.QuerySingleAsync<Guid?>(
            "SELECT UserId FROM dbo.Teachers WHERE Id = @teacherA", new { teacherA });
        linkedUserId.Should().Be(userA);

        var name = await conn.QuerySingleAsync<string?>(
            "SELECT Name FROM dbo.Users WHERE Id = @userA", new { userA });
        name.Should().Be("Clean Teacher");

        // -- Cross-tenant isolation: tenant A's teacher must not have linked to tenant B's user,
        // even though they share the same email. --
        var crossTenantLinkCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.Teachers WHERE Id = @teacherA AND UserId = @userBSameEmail",
            new { teacherA, userBSameEmail });
        crossTenantLinkCount.Should().Be(0);

        // -- Ambiguous: not linked, and reported with Reason='ambiguous', MatchCount=2. --
        var ambigLinked = await conn.QuerySingleAsync<Guid?>(
            "SELECT UserId FROM dbo.Teachers WHERE Id = @teacherAmbig", new { teacherAmbig });
        ambigLinked.Should().BeNull();

        var ambigReport = await conn.QuerySingleOrDefaultAsync(
            "SELECT Reason, MatchCount FROM dbo._Migration_UnmatchedDirectoryRows WHERE SourceTable = 'Teachers' AND SourceId = @teacherAmbig",
            new { teacherAmbig });
        ((object?)ambigReport).Should().NotBeNull("the migration's report INSERT should have recorded the ambiguous teacher");
        ((string)ambigReport!.Reason).Should().Be("ambiguous");
        ((int)ambigReport!.MatchCount).Should().Be(2);

        // -- No match: not linked, and reported with Reason='no_match', MatchCount=0. --
        var noMatchLinked = await conn.QuerySingleAsync<Guid?>(
            "SELECT UserId FROM dbo.Teachers WHERE Id = @teacherNoMatch", new { teacherNoMatch });
        noMatchLinked.Should().BeNull();

        var noMatchReport = await conn.QuerySingleOrDefaultAsync(
            "SELECT Reason, MatchCount FROM dbo._Migration_UnmatchedDirectoryRows WHERE SourceTable = 'Teachers' AND SourceId = @teacherNoMatch",
            new { teacherNoMatch });
        ((object?)noMatchReport).Should().NotBeNull();
        ((string)noMatchReport!.Reason).Should().Be("no_match");
        ((int)noMatchReport!.MatchCount).Should().Be(0);
    }
}
