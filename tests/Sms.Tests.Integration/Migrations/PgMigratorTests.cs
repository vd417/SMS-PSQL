using FluentAssertions;
using Npgsql;
using Sms.PgMigrator;

namespace Sms.Tests.Integration.Migrations;

/// PgMigrator against real Postgres, with a tiny synthetic baseline and synthetic migrations so
/// each behaviour is isolated from the real schema. Each test gets its own sms_migtest_* databases.
/// In the "sql" collection so it never runs concurrently with PostgresFixture's cluster-wide
/// CREATE ROLE sms_app.
[Collection("sql")]
public sealed class PgMigratorTests : IAsyncLifetime
{
    private const string SyntheticBaseline =
        """
        CREATE SCHEMA dbo;
        CREATE TABLE dbo."Tenants" ("Id" uuid PRIMARY KEY, "Name" text NOT NULL);
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("sms_pgmigrator_").FullName;
    private readonly List<MigrationTestDatabase> _dbs = [];
    private string BaselineDir => Path.Combine(_root, "baseline");
    private string MigrationsDir => Path.Combine(_root, "migrations");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(BaselineDir);
        Directory.CreateDirectory(MigrationsDir);
        File.WriteAllText(Path.Combine(BaselineDir, "01_baseline.sql"), SyntheticBaseline);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _dbs) await db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<MigrationTestDatabase> NewDbAsync()
    {
        var db = await MigrationTestDatabase.CreateAsync();
        _dbs.Add(db);
        return db;
    }

    /// A database that already has the baseline but has never seen the runner, like sms_dev today.
    private async Task<MigrationTestDatabase> BaselineOnlyDbAsync()
    {
        var db = await NewDbAsync();
        await db.ExecAsync(SyntheticBaseline);
        return db;
    }

    private void Migration(string fileName, string sql) => File.WriteAllText(Path.Combine(MigrationsDir, fileName), sql);

    private SchemaMigrator Runner(string connectionString, bool backupConfirmed = false, int lockTimeoutSeconds = 30) =>
        new(connectionString,
            new MigratorOptions(BaselineDir, MigrationsDir, TimeSpan.FromSeconds(lockTimeoutSeconds), backupConfirmed),
            TextWriter.Null);

    [Fact]
    public async Task Init_applies_the_baseline_then_every_migration_and_records_them()
    {
        var db = await NewDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int PRIMARY KEY);");

        var applied = await Runner(db.ConnectionString).InitAsync();

        applied.Should().Equal(1);
        (await db.ExistsAsync("dbo.\"Tenants\"")).Should().BeTrue();
        (await db.ExistsAsync("dbo.widgets")).Should().BeTrue();
        (await db.AppliedVersionsAsync()).Should().Equal(1);
        (await db.ScalarAsync<string>("SELECT checksum FROM dbo.schema_migrations WHERE version = 1"))
            .Should().Be(MigrationFile.ComputeChecksum("CREATE TABLE dbo.widgets (id int PRIMARY KEY);"));
    }

    [Fact]
    public async Task Init_with_the_real_baseline_builds_the_full_schema()
    {
        var db = await NewDbAsync();
        var real = new SchemaMigrator(db.ConnectionString,
            new MigratorOptions(Path.Combine(AppContext.BaseDirectory, "baseline"), MigrationsDir, TimeSpan.FromSeconds(30), false),
            TextWriter.Null);

        (await real.InitAsync()).Should().BeEmpty();

        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'dbo' AND tablename <> 'schema_migrations'"))
            .Should().BeGreaterThanOrEqualTo(121);
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_on_an_existing_database_applies_pending_without_rerunning_the_baseline()
    {
        var db = await BaselineOnlyDbAsync();
        await db.ExecAsync("""INSERT INTO dbo."Tenants" VALUES ('00000000-0000-0000-0000-000000000001', 'existing school')""");
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int PRIMARY KEY);");

        // Re-running the baseline would fail on CREATE SCHEMA dbo, so success alone proves it didn't.
        var applied = await Runner(db.ConnectionString).MigrateAsync();

        applied.Should().Equal(1);
        (await db.ScalarAsync<string>("""SELECT "Name" FROM dbo."Tenants" """)).Should().Be("existing school");
        (await db.AppliedVersionsAsync()).Should().Equal(1);
    }

    [Fact]
    public async Task Migrations_apply_in_numeric_order()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0010_ten.sql", "INSERT INTO dbo.steps (n) VALUES (10);");
        Migration("0002_two.sql", "INSERT INTO dbo.steps (n) VALUES (2);");
        Migration("0001_table.sql", "CREATE TABLE dbo.steps (seq serial PRIMARY KEY, n int NOT NULL);");

        (await Runner(db.ConnectionString).MigrateAsync()).Should().Equal(1, 2, 10);

        (await db.ScalarAsync<string>("SELECT string_agg(n::text, ',' ORDER BY seq) FROM dbo.steps")).Should().Be("2,10");
    }

    [Fact]
    public async Task Already_applied_migrations_are_skipped()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_seed.sql", "CREATE TABLE dbo.once (id int); INSERT INTO dbo.once VALUES (1);");

        (await Runner(db.ConnectionString).MigrateAsync()).Should().Equal(1);
        (await Runner(db.ConnectionString).MigrateAsync()).Should().BeEmpty();

        (await db.ScalarAsync<long>("SELECT count(*) FROM dbo.once")).Should().Be(1);
    }

    [Fact]
    public async Task Failed_migration_is_rolled_back_not_recorded_and_stops_the_run()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_ok.sql", "CREATE TABLE dbo.a (id int);");
        Migration("0002_broken.sql", "CREATE TABLE dbo.half_done (id int);\nSELECT 1 / 0;");
        Migration("0003_after.sql", "CREATE TABLE dbo.c (id int);");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        (await run.Should().ThrowAsync<MigrationException>())
            .WithMessage("*0002_broken.sql*NOT recorded*");
        (await db.AppliedVersionsAsync()).Should().Equal(1);
        (await db.ExistsAsync("dbo.half_done")).Should().BeFalse("the failed migration's DDL must roll back");
        (await db.ExistsAsync("dbo.c")).Should().BeFalse("no migration may run after a failure");

        // Not recorded, so it may be fixed in place. The lock was released, so a 1 s timeout is enough.
        Migration("0002_broken.sql", "CREATE TABLE dbo.half_done (id int);");
        (await Runner(db.ConnectionString, lockTimeoutSeconds: 1).MigrateAsync()).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Concurrent_runs_are_serialized_and_apply_each_migration_once()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_slow.sql", "SELECT pg_sleep(2); CREATE TABLE dbo.once (id int); INSERT INTO dbo.once VALUES (1);");

        var results = await Task.WhenAll(
            Runner(db.ConnectionString).MigrateAsync(),
            Runner(db.ConnectionString).MigrateAsync());

        results.Select(r => r.Count).Should().BeEquivalentTo([1, 0]);
        (await db.ScalarAsync<long>("SELECT count(*) FROM dbo.once")).Should().Be(1);
        (await db.AppliedVersionsAsync()).Should().Equal(1);
    }

    [Fact]
    public async Task A_run_gives_up_with_a_clear_error_when_the_lock_is_held()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({SchemaMigrator.AdvisoryLockKey})", holder))
            await take.ExecuteNonQueryAsync();

        var run = () => Runner(db.ConnectionString, lockTimeoutSeconds: 2).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*migration lock*Nothing was applied*");
        (await db.ExistsAsync("dbo.widgets")).Should().BeFalse();
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse();
    }

    [Fact]
    public async Task Migration_blocked_by_a_table_lock_fails_fast_instead_of_queueing_forever()
    {
        var db = await BaselineOnlyDbAsync();
        await db.ExecAsync("CREATE TABLE dbo.busy (id int);");
        Migration("0001_alter_busy.sql", "ALTER TABLE dbo.busy ADD COLUMN extra int;");
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand("LOCK TABLE dbo.busy IN ACCESS SHARE MODE", holder, tx))
            await take.ExecuteNonQueryAsync();

        // A long-running reader holds the table: without a lock_timeout the ALTER would wait
        // forever (and every later query on the table would queue behind it). The runner's
        // default per-migration lock_timeout is 10 s; allow generous slack before calling it a hang.
        var run = () => Runner(db.ConnectionString).MigrateAsync().WaitAsync(TimeSpan.FromSeconds(40));

        (await run.Should().ThrowAsync<MigrationException>())
            .WithMessage("*0001_alter_busy.sql*NOT recorded*lock timeout*");
        (await db.AppliedVersionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Init_refuses_a_database_that_already_has_the_baseline()
    {
        var db = await BaselineOnlyDbAsync();
        var run = () => Runner(db.ConnectionString).InitAsync();
        await run.Should().ThrowAsync<MigrationException>().WithMessage("*init refused*Use 'migrate'*");
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_refuses_an_empty_database()
    {
        var db = await NewDbAsync();
        var run = () => Runner(db.ConnectionString).MigrateAsync();
        await run.Should().ThrowAsync<MigrationException>().WithMessage("*migrate refused*Use 'init'*");
        (await db.ScalarAsync<bool>("SELECT to_regnamespace('dbo') IS NULL")).Should().BeTrue();
    }

    [Fact]
    public async Task Editing_an_applied_migration_is_refused()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await Runner(db.ConnectionString).MigrateAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id bigint);");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0001_widgets.sql*checksum*");
    }

    [Fact]
    public async Task Deleting_an_applied_migration_file_is_refused()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");
        await Runner(db.ConnectionString).MigrateAsync();
        File.Delete(Path.Combine(MigrationsDir, "0001_widgets.sql"));

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0001_widgets*file is missing*");
    }

    [Fact]
    public async Task Destructive_migration_requires_backup_confirmation_and_nothing_applies_without_it()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_add.sql", "CREATE TABLE dbo.x (id int, legacy text);");
        Migration("0002_drop.sql", "-- 0002: drop legacy column\n-- sms:destructive\nALTER TABLE dbo.x DROP COLUMN legacy;");

        var run = () => Runner(db.ConnectionString).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*0002_drop.sql*--backup-confirmed*Nothing was applied*");
        (await db.AppliedVersionsAsync()).Should().BeEmpty();
        (await db.ExistsAsync("dbo.x")).Should().BeFalse();

        (await Runner(db.ConnectionString, backupConfirmed: true).MigrateAsync()).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Status_is_read_only_and_reports_pending()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");

        var status = await Runner(db.ConnectionString).StatusAsync();

        status.HasBaseline.Should().BeTrue();
        status.Applied.Should().BeEmpty();
        status.Pending.Select(p => p.Version).Should().Equal(1);
        (await db.ExistsAsync("dbo.schema_migrations")).Should().BeFalse("status must not write");
    }

    [Fact]
    public async Task Migrate_as_the_app_role_is_refused_with_a_clear_message()
    {
        var db = await BaselineOnlyDbAsync();
        Migration("0001_widgets.sql", "CREATE TABLE dbo.widgets (id int);");

        var run = () => Runner(TestPostgresServer.AppConnectionString(db.Name)).MigrateAsync();

        await run.Should().ThrowAsync<MigrationException>().WithMessage("*schema owner*");
        (await db.ExistsAsync("dbo.widgets")).Should().BeFalse();
    }
}
