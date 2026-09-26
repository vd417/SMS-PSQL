using Sms.PgMigrator;
using Xunit;

namespace Sms.Tests.Integration;

/// Spins up a fresh, disposable Postgres database per test run and builds it through
/// Sms.PgMigrator's init (the immutable db/postgres/*.sql baseline, then every
/// db/postgres/migrations/*.sql forward migration, both copied next to the test assembly via the
/// Sms.PgMigrator project reference), exactly as a brand-new production database is built.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _dbName = "sms_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await TestPostgresServer.CreateDatabaseAsync(_dbName);

        // The same path production takes for a brand-new database: the immutable baseline, then
        // every forward migration, through the real runner, as the superuser (CREATE ROLE, RLS
        // and GRANT need it). Tests therefore always run against baseline + migrations.
        var migrator = new SchemaMigrator(
            TestPostgresServer.ForDatabase(_dbName),
            new MigratorOptions(
                Path.Combine(AppContext.BaseDirectory, "baseline"),
                Path.Combine(AppContext.BaseDirectory, "migrations"),
                TimeSpan.FromMinutes(1),
                BackupConfirmed: false),
            TextWriter.Null);
        await migrator.InitAsync();

        // The app itself (and therefore every test) connects as the non-superuser role from
        // here on, so RLS policies are genuinely exercised.
        ConnectionString = TestPostgresServer.AppConnectionString(_dbName);
    }

    public Task DisposeAsync() => TestPostgresServer.DropDatabaseAsync(_dbName);
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<PostgresFixture>;
