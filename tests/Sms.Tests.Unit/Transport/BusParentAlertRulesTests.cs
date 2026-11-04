using FluentAssertions;
using Sms.Modules.Transport;

namespace Sms.Tests.Unit.Transport;

public class BusParentAlertRulesTests
{
    [Fact]
    public void One_km_from_the_stop_is_in_range()
    {
        BusParentAlertRules.IsWithinApproach(1000).Should().BeTrue();
        BusParentAlertRules.IsWithinApproach(850).Should().BeTrue();
        BusParentAlertRules.IsWithinApproach(0).Should().BeTrue();
    }

    [Fact]
    public void Farther_than_one_km_is_not_in_range()
    {
        BusParentAlertRules.IsWithinApproach(1000.1).Should().BeFalse();
        BusParentAlertRules.IsWithinApproach(5000).Should().BeFalse();
    }

    [Fact]
    public void Accuracy_at_or_under_200m_is_accurate_enough()
    {
        BusParentAlertRules.IsAccurateEnough(200).Should().BeTrue();
        BusParentAlertRules.IsAccurateEnough(30).Should().BeTrue();
        BusParentAlertRules.IsAccurateEnough(0).Should().BeTrue();
    }

    [Fact]
    public void Accuracy_worse_than_200m_is_rejected()
    {
        BusParentAlertRules.IsAccurateEnough(200.1).Should().BeFalse();
        BusParentAlertRules.IsAccurateEnough(500).Should().BeFalse();
    }

    [Fact]
    public void Unknown_accuracy_is_treated_as_acceptable()
    {
        BusParentAlertRules.IsAccurateEnough(null).Should().BeTrue();
    }

    [Fact]
    public void A_fix_at_or_under_60s_old_is_fresh_enough()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        BusParentAlertRules.IsFreshEnough(now.AddSeconds(-60), now).Should().BeTrue();
        BusParentAlertRules.IsFreshEnough(now.AddSeconds(-30), now).Should().BeTrue();
        BusParentAlertRules.IsFreshEnough(now, now).Should().BeTrue();
    }

    [Fact]
    public void A_fix_older_than_60s_is_stale()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        BusParentAlertRules.IsFreshEnough(now.AddSeconds(-60.1), now).Should().BeFalse();
        BusParentAlertRules.IsFreshEnough(now.AddSeconds(-120), now).Should().BeFalse();
    }

    [Fact]
    public void A_missing_timestamp_is_never_fresh()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        BusParentAlertRules.IsFreshEnough(null, now).Should().BeFalse();
    }
}
