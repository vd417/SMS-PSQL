namespace Sms.Modules.Tenancy.Contracts;

// ---- Invoice ---- (Dapper maps columns straight onto this record)
public sealed record InvoiceResponse(
    Guid Id, Guid TenantId, string? TenantName, string? PlanName, decimal Amount,
    string Status, DateTime Issued, DateTime Due, DateTime? PaidOn);

public sealed record CreateInvoiceRequest(
    Guid TenantId, string? TenantName, string? PlanName, decimal Amount, DateTime Due);

// ---- Subscription ----
// Enriched for Billing → Subscriptions (joins tenant + plan). Wire names match catreadmin.
public sealed record SubscriptionResponse(
    Guid Id, Guid TenantId, string? TenantName, Guid PlanId, string? PlanName, string? Tier,
    string Status, DateTime CurrentPeriodStart, DateTime? CurrentPeriodEnd, decimal? NextCharge, int Seats);

public sealed record CreateSubscriptionRequest(Guid TenantId, Guid PlanId, int Seats);

/// <summary>Lean row returned by dbo.Subscription_Create before re-fetching the enriched view.</summary>
internal sealed record SubscriptionCreated(
    Guid Id, Guid TenantId, Guid PlanId, string Status, DateTime StartedAt, DateTime? RenewsAt, int Seats);

/// <summary>An active subscription whose current period has ended and is due for the next cycle invoice.</summary>
public sealed record DueSubscription(
    Guid Id, Guid TenantId, Guid PlanId, int Seats, string? BillingInterval, DateTime? RenewsAt);

// ---- Seat-increase requests (owner raises, CATRE approves) ----
public sealed record SeatRequestResponse(
    Guid Id, Guid TenantId, string? TenantName, int CurrentSeats, int RequestedSeats,
    string Status, string? Note, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>School owner raises a seat-increase request.</summary>
public sealed record RaiseSeatRequest(int RequestedSeats, string? Note = null);

/// <summary>CATRE approves/rejects a seat-increase request.</summary>
public sealed record ReviewSeatRequest(string? Note = null);

// ---- Dashboard overview ----
public sealed record DashCounts(int Total, int Active, int Trial, int Suspended, int Cancelled);
public sealed record PlanMixItem(string Label, int Value, string? Color);
public sealed record SystemHealthItem(string Name, string Status, string Latency, string Uptime);
public sealed record UsageAlertItem(string Tenant, string Metric, int Used, int Limit, int Pct);
public sealed record RecentActivityItem(string? Actor, string? Action, string? Target, string? Kind, DateTime At);

public sealed record DashboardOverview(
    DashCounts Counts, decimal Mrr, int TrialsEnding, decimal ChurnPct,
    IReadOnlyList<string> Months, IReadOnlyList<decimal> MrrSeries, IReadOnlyList<int> SignupSeries,
    IReadOnlyList<PlanMixItem> PlanMix, IReadOnlyList<UsageAlertItem> UsageAlerts,
    IReadOnlyList<SystemHealthItem> SystemHealth, IReadOnlyList<RecentActivityItem> RecentActivity);
