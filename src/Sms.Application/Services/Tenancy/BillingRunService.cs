using Microsoft.Extensions.Logging;
using Sms.Modules.Tenancy.Contracts;
using Sms.Modules.Tenancy.Data;

namespace Sms.Application.Services.Tenancy;

public interface IBillingRunService
{
    /// <summary>
    /// Generate the next cycle invoice for every active subscription whose renewal date has passed,
    /// then advance it to the following period. Idempotent per (tenant, period). Returns invoices created.
    /// Must run under a platform tenant context.
    /// </summary>
    Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct = default);
}

public sealed class BillingRunService(
    SubscriptionRepository subscriptions,
    ClientRepository clients,
    PlanRepository plans,
    InvoiceRepository invoices,
    ILogger<BillingRunService> logger) : IBillingRunService
{
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var due = await subscriptions.ListDueForBillingAsync(nowUtc, ct);
        var generated = 0;

        foreach (var sub in due)
        {
            if (ct.IsCancellationRequested) break;

            var periodStart = sub.RenewsAt ?? nowUtc;
            var months = CatreMappers.MonthsInCycle(sub.BillingInterval);
            var periodEnd = periodStart.AddMonths(months);

            try
            {
                // Idempotency: if this period is already billed, just advance the clock.
                if (await invoices.CycleInvoiceExistsAsync(sub.TenantId, periodStart, ct))
                {
                    await subscriptions.AdvanceRenewalAsync(sub.Id, periodEnd, periodStart, periodEnd, ct);
                    continue;
                }

                var client = await clients.GetAsync(sub.TenantId, ct);
                var plan = await plans.GetAsync(sub.PlanId, ct);
                if (client is null || plan is null)
                    continue; // orphaned subscription; leave it for inspection rather than billing blind.

                var amount = CatreMappers.ComputeCycleAmount(plan, client.StudentsCount, client.BillingCycle, sub.Seats);
                var seats = CatreMappers.BillableSeats(plan, client.StudentsCount, sub.Seats);

                var invoice = await invoices.CreateAsync(new CreateInvoiceRequest(
                    client.Id, client.Name, client.PlanName ?? plan.Name, amount,
                    nowUtc.Date.AddDays(14)), ct);
                if (invoice is not null)
                {
                    await invoices.SetCyclePeriodAsync(invoice.Id, periodStart, periodEnd, seats, ct);
                    generated++;
                }

                await subscriptions.AdvanceRenewalAsync(sub.Id, periodEnd, periodStart, periodEnd, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recurring billing failed for subscription {Subscription}", sub.Id);
            }
        }

        return generated;
    }
}
