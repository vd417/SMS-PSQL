using Npgsql;

namespace Sms.Tests.Integration;

/// Connection strings for the Postgres server integration tests run against, plus create/drop of
/// disposable databases. Every mutating entry point refuses any database that isn't a disposable
/// sms_test_* (PostgresFixture) or sms_migtest_* (runner tests) one, so no test can ever touch
/// sms_dev or any other shared database, whatever the environment variables point at.
public static class TestPostgresServer
{
    private static readonly string? OverrideCs = Environment.GetEnvironmentVariable("SMS_TEST_PG_CONNECTION");
    private static readonly string Host = Environment.GetEnvironmentVariable("SMS_TEST_PG_HOST") ?? "localhost";
    private static readonly string[] DisposablePrefixes = ["sms_test_", "sms_migtest_"];

    public static void EnsureDisposable(string db)
    {
        if (!DisposablePrefixes.Any(p => db.StartsWith(p, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Refusing to target database '{db}': tests may only touch disposable sms_test_*/sms_migtest_* databases.");
    }

    /// Owner (superuser) connection to a disposable test database: schema DDL, CREATE ROLE, GRANT.
    public static string ForDatabase(string db)
    {
        EnsureDisposable(db);
        return Build(db, username: null, password: null);
    }

    /// The app-facing non-superuser role (db/postgres/00_app_role.sql), so RLS actually applies.
    public static string AppConnectionString(string db)
    {
        EnsureDisposable(db);
        return Build(db, "sms_app", "sms_app_dev_pw");
    }

    public static async Task CreateDatabaseAsync(string db)
    {
        EnsureDisposable(db);
        await using var admin = new NpgsqlConnection(Build("postgres", null, null));
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        // Database names can't be parameterised; db is our own guid-based name (checked above).
        create.CommandText = $"CREATE DATABASE \"{db}\";";
        await create.ExecuteNonQueryAsync();
    }

    public static async Task DropDatabaseAsync(string db)
    {
        EnsureDisposable(db);
        await using var admin = new NpgsqlConnection(Build("postgres", null, null));
        await admin.OpenAsync();
        await using (var terminate = admin.CreateCommand())
        {
            terminate.CommandText =
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid();";
            terminate.Parameters.AddWithValue("db", db);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{db}\";";
        await drop.ExecuteNonQueryAsync();
    }

    private static string Build(string db, string? username, string? password)
    {
        var b = !string.IsNullOrEmpty(OverrideCs)
            ? new NpgsqlConnectionStringBuilder(OverrideCs) { Database = db }
            : new NpgsqlConnectionStringBuilder
            {
                Host = Host, Database = db, Username = "postgres",
                Password = Environment.GetEnvironmentVariable("SMS_TEST_PG_PASSWORD"),
            };
        if (username is not null) { b.Username = username; b.Password = password; }
        return b.ConnectionString;
    }
}
