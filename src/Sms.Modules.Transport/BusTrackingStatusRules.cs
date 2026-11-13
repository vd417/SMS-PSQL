namespace Sms.Modules.Transport;

/// Canonical parent/student tracking status. Snapshot motion (moving/stopped/offline) stays
/// on the live hub payload for existing teacher/admin clients.
public static class BusTrackingStatusRules
{
    public const int LiveSeconds = 60;
    public const int DelayedSeconds = 300;
    public const double StoppedSpeedKmh = 3;

    public const string Live = "LIVE";
    public const string Delayed = "DELAYED";
    public const string Offline = "OFFLINE";

    public const string Moving = "moving";
    public const string Stopped = "stopped";

    public const string LegacyIdle = "idle";
    public const string LegacyRunning = "running";
    public const string LegacyDelayed = "delayed";
    public const string LegacyAtStop = "at_stop";
    public const string LegacyOnRoute = "on_route";

    public const string Assigned = "assigned";
    public const string Pending = "pending";
    public const string None = "none";
    public const string OptedOut = "opted_out";

    public readonly record struct Result(string Tracking, string? Motion, string Legacy);

    public static Result Derive(
        DateTime utcNow, DateTime? lastPingAt, double? speedKmh, bool hasLiveTrip, bool gpsAllowed)
    {
        // No trip: idle and offline. While pickup or drop is still running, location
        // share stays live — a quiet or missing fix must not flip the app to offline.
        if (!hasLiveTrip)
            return new Result(Offline, null, LegacyIdle);
        if (!gpsAllowed || lastPingAt is null)
            return new Result(Live, null, LegacyRunning);

        var motion = speedKmh is <= StoppedSpeedKmh ? Stopped : Moving;
        return new Result(Live, motion, motion == Stopped ? LegacyAtStop : LegacyOnRoute);
    }

    public static string Assignment(bool optedOut, Guid? assignmentId, Guid? busId) =>
        optedOut ? OptedOut
        : assignmentId is null ? None
        : busId is null ? Pending
        : Assigned;
}
