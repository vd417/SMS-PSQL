using Npgsql;

namespace Sms.DevSeed;

public static class DevSeedGuard
{
    public const string ConfirmFlag = "--i-know-this-is-dev";

    /// Null when seeding is allowed; otherwise the refusal. Never echoes the connection string.
    public static string? Check(string? connectionString, IReadOnlyList<string> args)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return $"{Cli.ConnectionEnvVar} is not set. Set it to the schema-owner connection of a *_dev database " +
                   "(omit the password to use pgpass).";

        string? db;
        try { db = new NpgsqlConnectionStringBuilder(connectionString).Database; }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return $"{Cli.ConnectionEnvVar} is not a valid Npgsql connection string."; }

        if (string.IsNullOrEmpty(db) || !db.EndsWith("_dev", StringComparison.Ordinal))
            return $"Refusing to seed database '{db}': only a database whose name ends in _dev is allowed.";

        if (!args.Contains(ConfirmFlag))
            return $"Refusing to run without {ConfirmFlag}.";

        return null;
    }
}
