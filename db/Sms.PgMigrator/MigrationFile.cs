using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sms.PgMigrator;

/// One forward migration: db/postgres/migrations/NNNN_description.sql. Loading is strict on
/// purpose, so a misnamed, duplicated or self-committing file stops the run instead of being
/// silently skipped or half-applied.
public sealed partial record MigrationFile(int Version, string Name, string Sql, string Checksum, bool Destructive)
{
    /// Leading-comment directive marking a migration that drops or rewrites existing data;
    /// `migrate` then requires --backup-confirmed (docs/runbooks/postgres-migrations.md).
    public const string DestructiveDirective = "-- sms:destructive";

    public string FileName => $"{Version:D4}_{Name}.sql";

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();

    // The runner wraps each file in its own transaction together with its schema_migrations row.
    // A file that commits or opens a transaction itself would break that guarantee. PL/pgSQL
    // block BEGIN/END carry no trailing semicolon after BEGIN, and END is deliberately not
    // matched, so DO blocks and function bodies are unaffected.
    [GeneratedRegex(@"^\s*(BEGIN|COMMIT|ROLLBACK|START\s+TRANSACTION)(\s+(WORK|TRANSACTION))?\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TransactionControl();

    public static IReadOnlyList<MigrationFile> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new MigrationException($"Migrations directory not found: {directory}");

        var files = new List<MigrationFile>();
        foreach (var path in Directory.GetFiles(directory, "*.sql"))
        {
            var fileName = Path.GetFileName(path);
            var match = FileNamePattern().Match(fileName);
            var version = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            if (version < 1)
                throw new MigrationException(
                    $"'{fileName}' is not a valid migration name (expected NNNN_lowercase_description.sql, NNNN >= 0001). " +
                    "The runner never skips a .sql file silently; rename or remove it.");

            var sql = File.ReadAllText(path);
            if (TransactionControl().IsMatch(sql))
                throw new MigrationException(
                    $"'{fileName}' contains its own BEGIN/COMMIT/ROLLBACK. The runner already runs each migration in one " +
                    "transaction with its schema_migrations row; remove the transaction control statements.");

            files.Add(new MigrationFile(version, match.Groups[2].Value, sql, ComputeChecksum(sql), IsDestructive(sql)));
        }

        var duplicate = files.GroupBy(f => f.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new MigrationException(
                $"Duplicate migration version {duplicate.Key:D4}: {string.Join(", ", duplicate.Select(f => f.FileName))}");

        return files.OrderBy(f => f.Version).ToList();
    }

    /// SHA-256 of the file with CRLF normalised to LF, because Windows checkouts (core.autocrlf=true)
    /// and Linux CI/compose checkouts must agree.
    public static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n"))));

    private static bool IsDestructive(string sql)
    {
        foreach (var raw in sql.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("--", StringComparison.Ordinal)) return false;
            if (line.Equals(DestructiveDirective, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
