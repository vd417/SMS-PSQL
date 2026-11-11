using Sms.Application.Services.Reporting;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Api.Workers;

/// Mails saved school reports. Daily sends yesterday, weekly the previous 7 days,
/// 15 days the previous 15 days, and monthly the previous calendar month.
/// Uses the same mail sender as the rest of the API.
public sealed class ReportScheduleWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    IClock clock,
    ILogger<ReportScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("ReportSchedules:Enabled", true)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, true);
                var listing = scope.ServiceProvider.GetRequiredService<ISchoolReportService>();
                var due = await listing.ListDueAsync(clock.UtcNow, stoppingToken);
                foreach (var item in due)
                {
                    using var inner = scopeFactory.CreateScope();
                    inner.ServiceProvider.GetRequiredService<ITenantContext>().Set(item.TenantId, null, false);
                    await inner.ServiceProvider.GetRequiredService<ISchoolReportService>()
                        .SendDueAsync(item.Id, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Report schedule sweep failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
