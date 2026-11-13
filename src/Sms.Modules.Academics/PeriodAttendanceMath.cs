namespace Sms.Modules.Academics;

/// <summary>
/// Official student attendance % — period marks only.
/// (Present + Late) / All marked periods × 100. Unmarked excluded. Leave/Absent not positive.
/// </summary>
public static class PeriodAttendanceMath
{
    public const string Present = "present";
    public const string Late = "late";
    public const string Absent = "absent";
    public const string Leave = "leave";

    /// <summary>Returns null when there are no marked periods (do not treat as 0%).</summary>
    public static decimal? Percentage(int presentPeriods, int latePeriods, int totalMarkedPeriods)
    {
        if (totalMarkedPeriods <= 0) return null;
        var positive = presentPeriods + latePeriods;
        return Math.Round(100m * positive / totalMarkedPeriods, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Optional UI-only day badge: Present Today when (P+L)/marked ≥ 50% for that day.
    /// Null when the day has no marked periods.
    /// </summary>
    public static bool? PresentTodayBadge(int presentPeriods, int latePeriods, int totalMarkedPeriods)
    {
        if (totalMarkedPeriods <= 0) return null;
        var pct = 100m * (presentPeriods + latePeriods) / totalMarkedPeriods;
        return pct >= 50m;
    }

    /// <summary>
    /// One status for a student on one day. Present when (present + late) is at least half of
    /// that day's marks. Late only when they have no present mark. Absent otherwise.
    /// </summary>
    public static string? ClassifyStudentDay(int present, int late, int absent, int leave)
    {
        var marked = present + late + absent + leave;
        if (PresentTodayBadge(present, late, marked) is not true)
            return marked <= 0 ? null : Absent;
        if (present == 0 && late > 0) return Late;
        return Present;
    }

    public static PeriodAttendanceCounts FromStatusBuckets(
        int present, int late, int absent, int leave)
    {
        var marked = present + late + absent + leave;
        return new PeriodAttendanceCounts(marked, present, late, absent, leave,
            Percentage(present, late, marked));
    }
}

public readonly record struct PeriodAttendanceCounts(
    int TotalMarkedPeriods,
    int PresentPeriods,
    int LatePeriods,
    int AbsentPeriods,
    int LeavePeriods,
    decimal? AttendancePercentage);
