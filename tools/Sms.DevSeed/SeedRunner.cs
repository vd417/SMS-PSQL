using Npgsql;

namespace Sms.DevSeed;

public sealed record SeedReport(IReadOnlyDictionary<string, (int Inserted, int Skipped)> PerTable)
{
    public int TotalInserted => PerTable.Values.Sum(v => v.Inserted);
}

public static class SeedRunner
{
    /// One transaction. INSERT ... ON CONFLICT DO NOTHING only (never UPDATE/DELETE/TRUNCATE), then proves every
    /// seed row exists under its deterministic key — a row skipped because a NON-seed row holds one of its unique
    /// keys throws and rolls the whole run back.
    public static async Task<SeedReport> RunAsync(string cs, IReadOnlyList<SeedRow> rows, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformContext.ApplyAsync(conn, tx, ct);

        var stats = new Dictionary<string, (int Inserted, int Skipped)>();
        foreach (var row in rows)
        {
            var cols = row.Values.Keys.ToList();
            var sql = $"INSERT INTO \"dbo\".\"{row.Table}\" ({string.Join(", ", cols.Select(c => $"\"{c}\""))}) " +
                      $"VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))}) ON CONFLICT DO NOTHING";
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            for (var i = 0; i < cols.Count; i++) cmd.Parameters.AddWithValue($"p{i}", row.Values[cols[i]]);
            var inserted = await cmd.ExecuteNonQueryAsync(ct) == 1;
            stats.TryGetValue(row.Table, out var s);
            stats[row.Table] = inserted ? (s.Inserted + 1, s.Skipped) : (s.Inserted, s.Skipped + 1);
        }

        foreach (var row in rows)
            if (!await ExistsAsync(conn, tx, row, ct))
                throw new InvalidOperationException(
                    $"Seed row {row.Table}/{Describe(row)} was shadowed by a conflicting non-seed row; nothing was committed.");

        await tx.CommitAsync(ct);
        return new SeedReport(stats);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, SeedRow row, CancellationToken ct)
    {
        var keys = row.Values.ContainsKey("Id") ? new[] { "Id" } : new[] { "UserId", "Role" };
        var where = string.Join(" AND ", keys.Select((k, i) => $"\"{k}\" = @k{i}"));
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"dbo\".\"{row.Table}\" WHERE {where}", conn, tx);
        for (var i = 0; i < keys.Length; i++) cmd.Parameters.AddWithValue($"k{i}", row.Values[keys[i]]);
        return (long)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    private static string Describe(SeedRow row) =>
        row.Values.TryGetValue("Id", out var id) ? id.ToString()! : $"{row.Values["UserId"]}:{row.Values["Role"]}";
}

internal static class PlatformContext
{
    /// Forced RLS applies even to the table owner; platform context satisfies USING and WITH CHECK.
    public static async Task ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.is_platform', '1', true)", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
