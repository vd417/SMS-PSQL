namespace Sms.Modules.Sis.Contracts;

/// <summary>
/// Raw seat-usage figures read from the DB: billed limit, live active enrolment, alert gap,
/// the tenant's seat-increase mode ("auto" | "manual") and whether the plan is per-student.
/// </summary>
public sealed record SeatUsageRow(int Limit, int Used, int Threshold, string Mode, bool PerStudent);

/// <summary>
/// Seat usage for a school with derived flags. The billed seat <see cref="Limit"/> is the tenant's
/// <c>LimitsStudents</c> (copied from the plan). <see cref="Limit"/> &lt;= 0 means no cap (unlimited).
/// <see cref="AtLimit"/> drives the hard block on enrolment; <see cref="Warn"/> drives the
/// near-limit popup (configurable gap, default 5). <see cref="AutoIncrease"/> is true when the CATRE
/// team set the school to auto-grow seats (per-student plans only) instead of blocking.
/// </summary>
public sealed record SeatUsage(
    int Limit, int Used, int Remaining, int Threshold,
    bool Unlimited, bool AtLimit, bool Warn, bool AutoIncrease)
{
    public static SeatUsage From(SeatUsageRow r)
    {
        var unlimited = r.Limit <= 0;
        var remaining = unlimited ? 0 : Math.Max(r.Limit - r.Used, 0);
        var atLimit = !unlimited && r.Used >= r.Limit;
        var warn = !unlimited && (r.Limit - r.Used) <= r.Threshold;
        // Auto-grow only makes sense for per-student plans; flat bundles always need a manual bump.
        var auto = string.Equals(r.Mode, "auto", StringComparison.OrdinalIgnoreCase) && r.PerStudent;
        return new SeatUsage(r.Limit, r.Used, remaining, r.Threshold, unlimited, atLimit, warn, auto);
    }
}
