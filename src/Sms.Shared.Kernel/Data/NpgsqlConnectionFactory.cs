using System.Data.Common;
using Dapper;
using Npgsql;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Shared.Kernel.Data;

public sealed class NpgsqlConnectionFactory(string connectionString, ITenantContext tenant) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await StampTenantContextAsync(conn, ct);
        return conn;
    }

    // Session-scoped (is_local = false), not SET LOCAL/transaction-scoped: a pooled connection
    // must keep carrying the right tenant for every statement run on it, not just until the next
    // COMMIT — SET LOCAL would silently stop protecting later statements on the same connection.
    private async Task StampTenantContextAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (tenant.TenantId is { } tid)
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT set_config('app.tenant_id', @v, false)", new { v = tid.ToString() }, cancellationToken: ct));
        if (tenant.UserId is { } uid)
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT set_config('app.user_id', @v, false)", new { v = uid.ToString() }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.is_platform', @v, false)",
            new { v = tenant.IsPlatform ? "1" : "0" }, cancellationToken: ct));
    }
}
