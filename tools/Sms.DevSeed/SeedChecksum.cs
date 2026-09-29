using Npgsql;

namespace Sms.DevSeed;

/// Per-table row count + md5 of the seed-owned rows (seed tenants / seed users only) — proves a re-run changed nothing.
public static class SeedChecksum
{
    public static async Task<IReadOnlyDictionary<string, (long Count, string Md5)>> ComputeAsync(string cs, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformContext.ApplyAsync(conn, tx, ct);

        var result = new Dictionary<string, (long, string)>();
        foreach (var table in SeedData.Tables)
        {
            var filter = table switch
            {
                "Tenants" => "\"Id\" = ANY(@tenants)",
                "UserRoles" => "\"UserId\" = ANY(@users)",
                _ => "\"TenantId\" = ANY(@tenants)",
            };
            await using var cmd = new NpgsqlCommand(
                $"SELECT count(*), coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM \"dbo\".\"{table}\" t WHERE {filter}",
                conn, tx);
            cmd.Parameters.AddWithValue("tenants", new[] { SeedData.MainTenantId, SeedData.OtherTenantId });
            cmd.Parameters.AddWithValue("users", SeedData.SeedUserIds.ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            result[table] = (r.GetInt64(0), r.GetString(1));
        }
        await tx.RollbackAsync(ct);
        return result;
    }
}
