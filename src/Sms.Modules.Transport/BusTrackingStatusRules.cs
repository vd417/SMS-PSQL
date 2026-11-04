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
        // No trip: idle. A live trip with no fresh fix is RUNNING (legacy) and OFFLINE
        // for GPS. LIVE is reserved for a ping inside LiveSeconds. Coordinates are never invented.
        if (!hasLiveTrip)
            return new Result(Offline, null, LegacyIdle);
        if (!gpsAllowed || lastPingAt is null)
            return new Result(Offline, null, LegacyRunning);

        var ageSeconds = (utcNow - lastPingAt.Value).TotalSeconds;
        var motion = speedKmh is <= StoppedSpeedKmh ? Stopped : Moving;

        if (ageSeconds <= LiveSeconds)
            return new Result(Live, motion, motion == Stopped ? LegacyAtStop : LegacyOnRoute);

        if (ageSeconds <= DelayedSeconds)
            return new Result(Delayed, motion, LegacyDelayed);

        return new Result(Offline, motion, LegacyDelayed);
    }

    public static string Assignment(bool optedOut, Guid? assignmentId, Guid? busId) =>
        optedOut ? OptedOut
        : assignmentId is null ? None
        : busId is null ? Pending
        : Assigned;
}
