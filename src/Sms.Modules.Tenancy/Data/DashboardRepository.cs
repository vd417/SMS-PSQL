using Sms.Modules.Tenancy.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed class DashboardRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private sealed record CountsRow(int Total, int Active, int Trial, int Suspended, int Cancelled,
        decimal Mrr, int TrialsEnding, decimal ChurnPct);
    private sealed record PlanMixRow(string Label, int Value);
    private sealed record MonthRow(string Label, decimal Mrr, int Signups);

    /// One round-trip: counts+churn, plan mix, recent activity, usage alerts, monthly series.
    public async Task<DashboardOverview> OverviewAsync(CancellationToken ct = default)
    {
        // PL/pgSQL functions can only return one result set each, unlike the original
        // Dashboard_CatreOverview's 5-resultset QueryMultipleAsync round-trip -- split into 5
        // separate function calls (see db/postgres/18_catre_procs.sql).
        var c = await QuerySingleProcAsync<CountsRow>("dbo.dashboard_catreoverview_headline", null, ct)
            ?? new CountsRow(0, 0, 0, 0, 0, 0, 0, 0);
        var mix = (await QueryProcAsync<PlanMixRow>("dbo.dashboard_catreoverview_planmix", null, ct)).ToList();
        var activity = (await QueryProcAsync<RecentActivityItem>("dbo.dashboard_catreoverview_recentactivity", null, ct)).ToList();
        var alerts = (await QueryProcAsync<UsageAlertItem>("dbo.dashboard_catreoverview_usagealerts", null, ct)).ToList();
        var months = (await QueryProcAsync<MonthRow>("dbo.dashboard_catreoverview_months", null, ct)).ToList();

        return new DashboardOverview(
            new DashCounts(c.Total, c.Active, c.Trial, c.Suspended, c.Cancelled),
            c.Mrr, c.TrialsEnding, c.ChurnPct,
            Months: months.Select(m => m.Label).ToList(),
            MrrSeries: months.Select(m => m.Mrr).ToList(),
            SignupSeries: months.Select(m => m.Signups).ToList(),
            PlanMix: mix.Select(m => new PlanMixItem(m.Label, m.Value, null)).ToList(),
            UsageAlerts: alerts,
            SystemHealth: [new SystemHealthItem("Database", "operational", "-", "-")],
            RecentActivity: activity);
    }
}
