using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Integration.Migrations;

/// The real baseline and the real db/postgres/migrations, both as a fresh database (init) and as
/// an existing baseline-only database like sms_dev (migrate).
[Collection("sql")]
public sealed class RealMigrationsTests : IAsyncLifetime
{
    private static readonly string Baseline = Path.Combine(AppContext.BaseDirectory, "baseline");
    private static readonly string Migrations = Path.Combine(AppContext.BaseDirectory, "migrations");
    private readonly string _empty = Directory.CreateTempSubdirectory("sms_nomigrations_").FullName;
    private readonly List<MigrationTestDatabase> _dbs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _dbs) await db.DisposeAsync();
        Directory.Delete(_empty, recursive: true);
    }

    private async Task<MigrationTestDatabase> NewDbAsync()
    {
        var db = await MigrationTestDatabase.CreateAsync();
        _dbs.Add(db);
        return db;
    }

    private static SchemaMigrator Runner(MigrationTestDatabase db, string migrationsDir) =>
        new(db.ConnectionString, new MigratorOptions(Baseline, migrationsDir, TimeSpan.FromSeconds(60), false), TextWriter.Null);

    private static async Task AssertMigratedStateAsync(MigrationTestDatabase db)
    {
        (await db.ScalarAsync<string>("SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)"))
            .Should().Be("id uuid", "ClientRepository binds the argument as \"id\" => @Id");
        (await db.ScalarAsync<bool>("SELECT has_function_privilege('sms_app', 'dbo.client_delete(uuid)', 'EXECUTE')"))
            .Should().BeTrue();
        (await db.ScalarAsync<bool>("SELECT to_regprocedure('dbo.trip_ping_bulk_insert(uuid,uuid,jsonb)') IS NULL"))
            .Should().BeTrue("0002 drops the dead worked example");
        (await db.ScalarAsync<bool>("SELECT to_regprocedure('dbo.tripping_bulkinsert(uuid,uuid,text)') IS NOT NULL"))
            .Should().BeTrue("the live TripPing_BulkInsert function must be untouched");
    }

    [Fact]
    public async Task Init_applies_the_baseline_and_records_every_real_migration()
    {
        var db = await NewDbAsync();

        var applied = await Runner(db, Migrations).InitAsync();

        applied.Should().Equal(MigrationFile.LoadDirectory(Migrations).Select(m => m.Version));
        applied.Should().StartWith([1, 2]);
        (await db.AppliedVersionsAsync()).Should().Equal(applied);
        await AssertMigratedStateAsync(db);
    }

    [Fact]
    public async Task Migrate_brings_a_baseline_only_database_up_to_date()
    {
        var db = await NewDbAsync();
        (await Runner(db, _empty).InitAsync()).Should().BeEmpty();
        (await db.ScalarAsync<string>("SELECT pg_get_function_identity_arguments('dbo.client_delete'::regproc)"))
            .Should().Be("p_id uuid", "precondition: the unfixed baseline signature");

        var applied = await Runner(db, Migrations).MigrateAsync();

        applied.Should().StartWith([1, 2]);
        await AssertMigratedStateAsync(db);
    }
}
