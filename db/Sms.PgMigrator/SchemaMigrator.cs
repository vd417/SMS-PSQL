using Npgsql;

namespace Sms.PgMigrator;

public sealed record MigratorOptions(string BaselineDirectory, string MigrationsDirectory, TimeSpan LockTimeout, bool BackupConfirmed);

public sealed record AppliedMigration(int Version, string Name, string Checksum, DateTime AppliedAt);

public sealed record MigrationStatus(bool HasBaseline, IReadOnlyList<AppliedMigration> Applied, IReadOnlyList<MigrationFile> Pending);

/// Forward-only Postgres schema runner.
///   init    - empty database: the immutable db/postgres/*.sql baseline (one transaction), then every migration.
///   migrate - database that already has the baseline: pending db/postgres/migrations/*.sql only.
///   status  - read-only report.
/// Each migration runs in its own transaction together with its dbo.schema_migrations row, so a
/// failure leaves it unrecorded with none of its DDL applied. Runs against one database are
/// serialised with a session advisory lock. Connect as the schema owner, never as sms_app.
public sealed class SchemaMigrator(string connectionString, MigratorOptions options, TextWriter log)
{
    /// Fixed forever: every runner version must agree on it or two deploys could run at once.
    public const long AdvisoryLockKey = 7_365_202_600_001;

    private const string TrackingTableDdl =
        """
        CREATE TABLE IF NOT EXISTS dbo.schema_migrations (
            version     int PRIMARY KEY,
            name        text NOT NULL,
            checksum    text NOT NULL,
            applied_at  timestamptz NOT NULL DEFAULT now()
        );
        """;

    public async Task<IReadOnlyList<int>> InitAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        var baseline = LoadBaseline();
        await using var conn = await OpenAsync(ct);
        await using var _ = await AdvisoryLock.AcquireAsync(conn, AdvisoryLockKey, options.LockTimeout, log, ct);

        if (await ScalarAsync<bool>(conn, "SELECT to_regnamespace('dbo') IS NOT NULL", ct))
            throw new MigrationException(
                "init refused: the target already has a dbo schema. Use 'migrate' for an existing database. Nothing was changed.");

        log.WriteLine($"Fresh database: applying {baseline.Count} baseline file(s) in one transaction.");
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            foreach (var (name, sql) in baseline)
            {
                try
                {
                    await ExecAsync(conn, tx, sql, ct);
                }
                catch (PostgresException ex)
                {
                    throw new MigrationException(
                        $"init failed in baseline file {name}; the whole baseline was rolled back and the database is still empty. {ex.MessageText}", ex);
                }
                log.WriteLine($"  baseline {name}");
            }
            await ExecAsync(conn, tx, TrackingTableDdl, ct);
            await tx.CommitAsync(ct);
        }

        return await ApplyPendingAsync(conn, migrations, requireBackupForDestructive: false, ct);
    }

    public async Task<IReadOnlyList<int>> MigrateAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        await using var conn = await OpenAsync(ct);
        await using var _ = await AdvisoryLock.AcquireAsync(conn, AdvisoryLockKey, options.LockTimeout, log, ct);

        if (!await HasBaselineAsync(conn, ct))
            throw new MigrationException(
                "migrate refused: the target has no baseline (dbo.\"Tenants\" does not exist). Use 'init' for an empty database. Nothing was changed.");

        try
        {
            await ExecAsync(conn, null, TrackingTableDdl, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new MigrationException(
                "Could not create dbo.schema_migrations: this role lacks DDL rights. Connect as the schema owner " +
                $"(never the API's sms_app role). Nothing was applied. {ex.MessageText}", ex);
        }

        return await ApplyPendingAsync(conn, migrations, requireBackupForDestructive: true, ct);
    }

    public async Task<MigrationStatus> StatusAsync(CancellationToken ct = default)
    {
        var migrations = MigrationFile.LoadDirectory(options.MigrationsDirectory);
        await using var conn = await OpenAsync(ct);
        var hasBaseline = await HasBaselineAsync(conn, ct);
        var applied = await ReadAppliedAsync(conn, ct);
        VerifyHistory(migrations, applied);
        var pending = migrations.Where(m => applied.All(a => a.Version != m.Version)).ToList();

        log.WriteLine(hasBaseline ? "Baseline: present." : "Baseline: MISSING (empty database, use init).");
        foreach (var a in applied) log.WriteLine($"  applied  {a.Version:D4}_{a.Name}  {a.AppliedAt:u}");
        foreach (var p in pending) log.WriteLine($"  pending  {p.FileName}{(p.Destructive ? "  [destructive]" : "")}");
        if (pending.Count == 0) log.WriteLine("No pending migrations.");
        return new MigrationStatus(hasBaseline, applied, pending);
    }

    private async Task<IReadOnlyList<int>> ApplyPendingAsync(
        NpgsqlConnection conn, IReadOnlyList<MigrationFile> migrations, bool requireBackupForDestructive, CancellationToken ct)
    {
        var applied = await ReadAppliedAsync(conn, ct);
        VerifyHistory(migrations, applied);
        var pending = migrations.Where(m => applied.All(a => a.Version != m.Version)).ToList();

        if (requireBackupForDestructive && !options.BackupConfirmed && pending.FirstOrDefault(m => m.Destructive) is { } destructive)
            throw new MigrationException(
                $"{destructive.FileName} is marked '{MigrationFile.DestructiveDirective}'. Take and verify a backup " +
                "(docs/runbooks/postgres-migrations.md), then re-run with --backup-confirmed. Nothing was applied.");

        if (pending.Count == 0)
        {
            log.WriteLine("No pending migrations.");
            return [];
        }

        var done = new List<int>();
        foreach (var m in pending)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await ExecAsync(conn, tx, m.Sql, ct);
                await using var record = new NpgsqlCommand(
                    "INSERT INTO dbo.schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", conn, tx);
                record.Parameters.AddWithValue("v", m.Version);
                record.Parameters.AddWithValue("n", m.Name);
                record.Parameters.AddWithValue("c", m.Checksum);
                await record.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception) { /* connection lost: server rolls back */ }
                throw new MigrationException(
                    $"{m.FileName} failed and was rolled back; it is NOT recorded as applied and no later migration ran. " +
                    (ex is PostgresException pg ? pg.MessageText : ex.Message), ex);
            }
            log.WriteLine($"Applied {m.FileName}");
            done.Add(m.Version);
        }
        return done;
    }

    private static void VerifyHistory(IReadOnlyList<MigrationFile> files, IReadOnlyList<AppliedMigration> applied)
    {
        foreach (var a in applied)
        {
            var file = files.FirstOrDefault(f => f.Version == a.Version)
                ?? throw new MigrationException(
                    $"Migration {a.Version:D4}_{a.Name} is recorded as applied but its file is missing. Applied migrations must never be deleted.");
            if (file.Checksum != a.Checksum)
                throw new MigrationException(
                    $"{file.FileName} was edited after it was applied (checksum mismatch). Never edit an applied migration; add a new one.");
        }
    }

    private IReadOnlyList<(string Name, string Sql)> LoadBaseline()
    {
        if (!Directory.Exists(options.BaselineDirectory))
            throw new MigrationException($"Baseline directory not found: {options.BaselineDirectory}");
        var files = Directory.GetFiles(options.BaselineDirectory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        if (files.Count == 0)
            throw new MigrationException($"Baseline directory has no .sql files: {options.BaselineDirectory}");
        return files;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        // No pooling: the advisory lock is tied to this one physical session.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT current_database(), current_user::text, current_setting('server_version')", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        log.WriteLine($"Target: host={builder.Host} port={builder.Port} database={r.GetString(0)} user={r.GetString(1)} server=PostgreSQL {r.GetString(2)}");
        return conn;
    }

    // Existence checks read pg_catalog directly (readable by every role), not to_regclass, so a
    // role without USAGE on dbo gets the clear privilege error below instead of "no baseline".
    private static Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct) =>
        ScalarAsync<bool>(conn,
            $"SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'dbo' AND c.relname = '{table}')",
            ct);

    private static Task<bool> HasBaselineAsync(NpgsqlConnection conn, CancellationToken ct) =>
        TableExistsAsync(conn, "Tenants", ct);

    private static async Task<IReadOnlyList<AppliedMigration>> ReadAppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "schema_migrations", ct))
            return [];
        var rows = new List<AppliedMigration>();
        await using var cmd = new NpgsqlCommand(
            "SELECT version, name, checksum, applied_at FROM dbo.schema_migrations ORDER BY version", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new AppliedMigration(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDateTime(3)));
        return rows;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        // No timeout: schema changes on real data can legitimately run long.
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
