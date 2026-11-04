using FluentAssertions;
using Sms.Modules.Transport;

namespace Sms.Tests.Unit.Transport;

public class BusTrackingStatusRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_moving_ping_is_live_on_route()
    {
        var r = BusTrackingStatusRules.Derive(Now, Now.AddSeconds(-12), 28, hasLiveTrip: true, gpsAllowed: true);
        r.Tracking.Should().Be("LIVE");
        r.Motion.Should().Be("moving");
        r.Legacy.Should().Be("on_route");
    }

    [Fact]
    public void Fresh_slow_ping_is_live_at_stop()
    {
        var r = BusTrackingStatusRules.Derive(Now, Now.AddSeconds(-5), 2, hasLiveTrip: true, gpsAllowed: true);
        r.Tracking.Should().Be("LIVE");
        r.Motion.Should().Be("stopped");
        r.Legacy.Should().Be("at_stop");
    }

    [Fact]
    public void Ping_older_than_a_minute_but_under_five_is_delayed()
    {
        var r = BusTrackingStatusRules.Derive(Now, Now.AddSeconds(-90), 20, hasLiveTrip: true, gpsAllowed: true);
        r.Tracking.Should().Be("DELAYED");
        r.Motion.Should().Be("moving");
        r.Legacy.Should().Be("delayed");
    }

    [Fact]
    public void Ping_older_than_five_minutes_is_offline_with_last_known_motion()
    {
        var r = BusTrackingStatusRules.Derive(Now, Now.AddMinutes(-8), 18, hasLiveTrip: true, gpsAllowed: true);
        r.Tracking.Should().Be("OFFLINE");
        r.Legacy.Should().Be("delayed");
    }

    [Fact]
    public void No_trip_or_gps_lock_is_offline_idle()
    {
        BusTrackingStatusRules.Derive(Now, Now, 30, hasLiveTrip: false, gpsAllowed: true)
            .Legacy.Should().Be("idle");
        var started = BusTrackingStatusRules.Derive(Now, null, null, hasLiveTrip: true, gpsAllowed: true);
        started.Tracking.Should().Be("OFFLINE");
        started.Legacy.Should().Be("running");
        BusTrackingStatusRules.Derive(Now, Now, 30, hasLiveTrip: true, gpsAllowed: false)
            .Legacy.Should().Be("running");
    }

    [Fact]
    public void Assignment_covers_opt_out_pending_and_mapped()
    {
        BusTrackingStatusRules.Assignment(true, Guid.NewGuid(), Guid.NewGuid()).Should().Be("opted_out");
        BusTrackingStatusRules.Assignment(false, null, null).Should().Be("none");
        BusTrackingStatusRules.Assignment(false, Guid.NewGuid(), null).Should().Be("pending");
        BusTrackingStatusRules.Assignment(false, Guid.NewGuid(), Guid.NewGuid()).Should().Be("assigned");
    }
}
