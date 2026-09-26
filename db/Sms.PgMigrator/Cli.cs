using Npgsql;

namespace Sms.PgMigrator;

public sealed record CliOptions(
    string Command, string ConnectionString, string BaselineDirectory, string MigrationsDirectory,
    TimeSpan LockTimeout, bool BackupConfirmed);

/// Entry point. Exit codes: 0 success, 1 usage error, 2 refused or failed (the message says what
/// state the database is in). See docs/runbooks/postgres-migrations.md.
public static class Cli
{
    /// Deliberately not ConnectionStrings__Sql: that is the API's non-superuser sms_app string, and
    /// migrations need the schema owner.
    public const string ConnectionEnvVar = "SMS_MIGRATOR_CONNECTION";

    private const string Usage =
        """
        Usage: Sms.PgMigrator <init|migrate|status> [--connection "<npgsql connection string>"]
                              [--baseline-dir <dir>] [--migrations-dir <dir>]
                              [--lock-timeout-seconds <n>] [--backup-confirmed]
          init     empty database    -> baseline (db/postgres/*.sql) + every migration
          migrate  existing database -> pending migrations (db/postgres/migrations/*.sql) only
          status   read-only: applied and pending migrations
        The connection falls back to SMS_MIGRATOR_CONNECTION and must be the schema owner role,
        never the API's sms_app role.
        """;

    public static async Task<int> Main(string[] args)
    {
        var o = Parse(args, Environment.GetEnvironmentVariable(ConnectionEnvVar), AppContext.BaseDirectory, out var error);
        if (o is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var migrator = new SchemaMigrator(o.ConnectionString,
            new MigratorOptions(o.BaselineDirectory, o.MigrationsDirectory, o.LockTimeout, o.BackupConfirmed), Console.Out);
        try
        {
            switch (o.Command)
            {
                case "init": await migrator.InitAsync(); break;
                case "migrate": await migrator.MigrateAsync(); break;
                default: await migrator.StatusAsync(); break;
            }
            return 0;
        }
        catch (MigrationException ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 2;
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine($"FAILED: database error, nothing further was attempted: {ex.Message}");
            return 2;
        }
    }

    public static CliOptions? Parse(string[] args, string? envConnection, string baseDirectory, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0] is not ("init" or "migrate" or "status"))
        {
            error = "The first argument must be init, migrate or status.";
            return null;
        }

        string? connection = null;
        var baseline = Path.Combine(baseDirectory, "baseline");
        var migrations = Path.Combine(baseDirectory, "migrations");
        var lockSeconds = 60;
        var backupConfirmed = false;

        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i];
            if (flag == "--backup-confirmed")
            {
                backupConfirmed = true;
                continue;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{flag}' needs a value.";
                return null;
            }
            var value = args[++i];
            switch (flag)
            {
                case "--connection": connection = value; break;
                case "--baseline-dir": baseline = value; break;
                case "--migrations-dir": migrations = value; break;
                case "--lock-timeout-seconds" when int.TryParse(value, out var s) && s > 0: lockSeconds = s; break;
                default:
                    error = $"Unknown option '{flag}' or invalid value '{value}'.";
                    return null;
            }
        }

        connection = string.IsNullOrWhiteSpace(connection) ? envConnection : connection;
        if (string.IsNullOrWhiteSpace(connection))
        {
            error = $"No connection string: pass --connection or set {ConnectionEnvVar}.";
            return null;
        }

        return new CliOptions(args[0], connection, baseline, migrations, TimeSpan.FromSeconds(lockSeconds), backupConfirmed);
    }
}
