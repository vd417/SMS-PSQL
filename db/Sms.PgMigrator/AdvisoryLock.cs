using Npgsql;

namespace Sms.PgMigrator;

/// Session-level advisory lock serialising migration runs against one database (advisory locks
/// are per database). Released explicitly on dispose; Postgres also drops it if the connection dies.
internal sealed class AdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _conn;
    private readonly long _key;

    private AdvisoryLock(NpgsqlConnection conn, long key) { _conn = conn; _key = key; }

    public static async Task<AdvisoryLock> AcquireAsync(
        NpgsqlConnection conn, long key, TimeSpan timeout, TextWriter log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var announced = false;
        while (true)
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
            {
                cmd.Parameters.AddWithValue("k", key);
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
                    return new AdvisoryLock(conn, key);
            }
            if (DateTime.UtcNow >= deadline)
                throw new MigrationException(
                    $"Another run holds the migration lock on this database; gave up after {timeout.TotalSeconds:0}s. Nothing was applied.");
            if (!announced)
            {
                log.WriteLine("Another migration run is in progress; waiting for its lock...");
                announced = true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn.State != System.Data.ConnectionState.Open) return;
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", _conn);
            cmd.Parameters.AddWithValue("k", _key);
            await cmd.ExecuteScalarAsync();
        }
        catch (NpgsqlException)
        {
            // Connection is going away; Postgres releases session locks with it.
        }
    }
}
