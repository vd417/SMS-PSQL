namespace Sms.Modules.Sis.Contracts;

/// <summary>Raw seat-usage figures read from the DB: billed limit, live active enrolment, alert gap.</summary>
public sealed record SeatUsageRow(int Limit, int Used, int Threshold);

/// <summary>
/// Seat usage for a school with derived flags. The billed seat <see cref="Limit"/> is the tenant's
/// <c>LimitsStudents</c> (copied from the plan). <see cref="Limit"/> &lt;= 0 means no cap (unlimited).
/// <see cref="AtLimit"/> drives the hard block on enrolment; <see cref="Warn"/> drives the
/// near-limit popup (configurable gap, default 5).
/// </summary>
public sealed record SeatUsage(
    int Limit, int Used, int Remaining, int Threshold, bool Unlimited, bool AtLimit, bool Warn)
{
    public static SeatUsage From(SeatUsageRow r)
    {
        var unlimited = r.Limit <= 0;
        var remaining = unlimited ? 0 : Math.Max(r.Limit - r.Used, 0);
        var atLimit = !unlimited && r.Used >= r.Limit;
        var warn = !unlimited && (r.Limit - r.Used) <= r.Threshold;
        return new SeatUsage(r.Limit, r.Used, remaining, r.Threshold, unlimited, atLimit, warn);
    }
}
