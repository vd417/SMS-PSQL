using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Sms.Api.Http;

/// Readiness probe: opens a plain connection (no tenant context) and runs SELECT 1.
/// Healthy = DB reachable; Unhealthy = DB down (maps to 503 on /health/ready).
public sealed class SqlHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}
