using Npgsql;
using Xunit;

namespace Sms.Tests.Integration;

/// Spins up a fresh, disposable Postgres database per test run and applies the schema from
/// db/postgres/*.sql (copied next to the test assembly at build time — see the .csproj) in name
/// order. Replaces the old SQL-Server-based fixture that ran the retired FluentMigrator history.
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string? _overrideCs =
        Environment.GetEnvironmentVariable("SMS_TEST_PG_CONNECTION");
    private readonly string _host =
        Environment.GetEnvironmentVariable("SMS_TEST_PG_HOST") ?? "localhost";
    private readonly string _dbName = "sms_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";

    private string AdminCs =>
        !string.IsNullOrEmpty(_overrideCs)
            ? new NpgsqlConnectionStringBuilder(_overrideCs) { Database = "postgres" }.ConnectionString
            : new NpgsqlConnectionStringBuilder
            {
                Host = _host, Database = "postgres", Username = "postgres",
                Password = Environment.GetEnvironmentVariable("SMS_TEST_PG_PASSWORD"),
            }.ConnectionString;

    private string DbCs(string db) =>
        !string.IsNullOrEmpty(_overrideCs)
            ? new NpgsqlConnectionStringBuilder(_overrideCs) { Database = db }.ConnectionString
            : new NpgsqlConnectionStringBuilder
            {
                Host = _host, Database = db, Username = "postgres",
                Password = Environment.GetEnvironmentVariable("SMS_TEST_PG_PASSWORD"),
            }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminCs))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            // Database names can't be parameterised; _dbName is our own generated guid-based
            // name, never external input, so this is not a SQL-injection boundary.
            create.CommandText = $"CREATE DATABASE \"{_dbName}\";";
            await create.ExecuteNonQueryAsync();
        }

        ConnectionString = DbCs(_dbName);

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "postgres-schema");
        var files = Directory.GetFiles(schemaDir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var file in files)
        {
            await using var batch = conn.CreateCommand();
            batch.CommandText = await File.ReadAllTextAsync(file);
            await batch.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(AdminCs);
        await admin.OpenAsync();
        await using var terminate = admin.CreateCommand();
        terminate.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();";
        terminate.Parameters.AddWithValue("db", _dbName);
        await terminate.ExecuteNonQueryAsync();

        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\";";
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<PostgresFixture>;
