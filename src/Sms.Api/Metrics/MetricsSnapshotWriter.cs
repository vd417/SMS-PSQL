using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Metrics;

/// Upserts the current month's platform metrics snapshot at startup (idempotent).
/// Historical months accumulate boot-over-boot; the current month is always refreshed.
public static class MetricsSnapshotWriter
{
    public static async Task RunAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MetricsSnapshotWriter");

        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.Set(null, null, isPlatform: true);
            var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

            await using var conn = await factory.OpenAsync();
            await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, new Dapper.CommandDefinition(
                "dbo.platformmetrics_upsertcurrentmonth",
                commandType: System.Data.CommandType.StoredProcedure));
            log.LogInformation("Platform metrics snapshot upserted for the current month.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Metrics snapshot refresh failed; continuing startup.");
            return;
        }
    }
}
