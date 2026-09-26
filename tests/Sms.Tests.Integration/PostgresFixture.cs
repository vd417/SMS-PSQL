using Npgsql;
using Xunit;

namespace Sms.Tests.Integration;

/// Spins up a fresh, disposable Postgres database per test run and applies the schema from
/// db/postgres/*.sql (copied next to the test assembly at build time — see the .csproj) in name
/// order. Replaces the old SQL-Server-based fixture that ran the retired FluentMigrator history.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _dbName = "sms_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await TestPostgresServer.CreateDatabaseAsync(_dbName);

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "postgres-schema");
        var files = Directory.GetFiles(schemaDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

        // Schema application (CREATE ROLE, ENABLE/FORCE RLS, GRANT) needs superuser privilege.
        await using (var conn = new NpgsqlConnection(TestPostgresServer.ForDatabase(_dbName)))
        {
            await conn.OpenAsync();
            foreach (var file in files)
            {
                await using var batch = conn.CreateCommand();
                batch.CommandText = await File.ReadAllTextAsync(file);
                await batch.ExecuteNonQueryAsync();
            }
        }

        // The app itself (and therefore every test) connects as the non-superuser role from
        // here on, so RLS policies are genuinely exercised.
        ConnectionString = TestPostgresServer.AppConnectionString(_dbName);
    }

    public Task DisposeAsync() => TestPostgresServer.DropDatabaseAsync(_dbName);
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<PostgresFixture>;
