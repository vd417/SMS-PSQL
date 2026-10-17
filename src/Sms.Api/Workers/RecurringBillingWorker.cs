using Sms.Application.Services.Tenancy;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Workers;

/// <summary>
/// Server-side recurring billing scheduler. On a low-frequency poll it finds active subscriptions
/// whose renewal date has passed and issues the next cycle invoice (monthly / quarterly / yearly),
/// then advances the subscription to the following period. Idempotent per (tenant, period), so a
/// re-run never double-bills.
///
/// Gated by Billing:RecurringEnabled (default on) so integration tests can switch it off and drive
/// IBillingRunService directly, exactly like TransportOfflineSweepWorker.
/// </summary>
public sealed class RecurringBillingWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<RecurringBillingWorker> logger) : BackgroundService
{
    private readonly TimeSpan _poll =
        TimeSpan.FromHours(Math.Clamp(config.GetValue<int?>("Billing:PollHours") ?? 6, 1, 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, true);
                var svc = scope.ServiceProvider.GetRequiredService<IBillingRunService>();
                var count = await svc.RunDueAsync(DateTime.UtcNow, stoppingToken);
                if (count > 0)
                    logger.LogInformation("Recurring billing: generated {Count} cycle invoice(s)", count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Recurring billing sweep failed"); }

            try { await Task.Delay(_poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
