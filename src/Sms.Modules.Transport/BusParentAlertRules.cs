namespace Sms.Modules.Transport;

/// Parent live-bus alerts: a trip that has started, or a bus entering the student's stop radius.
public static class BusParentAlertRules
{
    public const string TripStarted = "trip_started";
    public const string ApproachingStop = "approaching_stop";
    /// A trip that has ended — teacher alerts only (parents are not notified on trip end).
    public const string TripEnded = "trip_ended";

    /// Default: notify when the bus is within 1 km of the child's assigned stop.
    public const double DefaultApproachMeters = 1000;

    /// Skip the alert when the last ping's reported accuracy is worse than this — an
    /// obviously unreliable fix (city GPS is normally well under ~30 m).
    public const double MaxAccuracyMeters = 200;

    /// Skip the alert when the last ping is older than this — a stale position must
    /// not produce a fresh "1 km away" notice.
    public const double MaxStalenessSeconds = 60;

    public static bool IsWithinApproach(double distanceMeters, double radiusMeters = DefaultApproachMeters) =>
        distanceMeters <= radiusMeters && distanceMeters >= 0;

    /// True when accuracy is good enough to trust. Unknown (null) accuracy is treated
    /// as acceptable so pings that don't report accuracy keep working as before.
    public static bool IsAccurateEnough(double? accuracyMeters, double maxAccuracyMeters = MaxAccuracyMeters) =>
        accuracyMeters is not { } a || a <= maxAccuracyMeters;

    /// True when the last ping is recent enough to base a "1 km away" alert on.
    /// A missing timestamp is never fresh.
    public static bool IsFreshEnough(DateTime? lastPingAt, DateTime nowUtc, double maxStalenessSeconds = MaxStalenessSeconds) =>
        lastPingAt is { } t && (nowUtc - t).TotalSeconds <= maxStalenessSeconds;
}
