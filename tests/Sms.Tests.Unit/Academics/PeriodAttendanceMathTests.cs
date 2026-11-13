using FluentAssertions;
using Sms.Modules.Academics;

namespace Sms.Tests.Unit.Academics;

public class PeriodAttendanceMathTests
{
    [Theory]
    [InlineData(8, 1, 2, 0, 81.82)]
    [InlineData(10, 0, 0, 0, 100.00)]
    [InlineData(5, 2, 3, 0, 70.00)]
    [InlineData(15, 2, 3, 0, 85.00)]
    [InlineData(35, 2, 5, 0, 88.10)]
    public void Percentage_matches_official_formula(
        int present, int late, int absent, int leave, double expected)
    {
        var counts = PeriodAttendanceMath.FromStatusBuckets(present, late, absent, leave);
        counts.AttendancePercentage.Should().Be((decimal)expected);
        counts.TotalMarkedPeriods.Should().Be(present + late + absent + leave);
    }

    [Fact]
    public void Percentage_null_when_no_marked_periods()
    {
        PeriodAttendanceMath.Percentage(0, 0, 0).Should().BeNull();
        PeriodAttendanceMath.FromStatusBuckets(0, 0, 0, 0).AttendancePercentage.Should().BeNull();
    }

    [Fact]
    public void Unmarked_periods_are_not_in_denominator()
    {
        // Present=10, unmarked=10 → still 100% (only marked count)
        var pct = PeriodAttendanceMath.Percentage(presentPeriods: 10, latePeriods: 0, totalMarkedPeriods: 10);
        pct.Should().Be(100.00m);
    }

    [Fact]
    public void Leave_and_absent_are_not_positive()
    {
        var counts = PeriodAttendanceMath.FromStatusBuckets(present: 1, late: 0, absent: 1, leave: 1);
        counts.AttendancePercentage.Should().Be(33.33m);
        counts.TotalMarkedPeriods.Should().Be(3);
    }

    [Theory]
    [InlineData(7, 0, 1, 0, "present")]
    [InlineData(8, 0, 0, 0, "present")]
    [InlineData(0, 0, 5, 0, "absent")]
    [InlineData(0, 1, 0, 0, "late")]
    [InlineData(1, 1, 0, 0, "present")]
    [InlineData(0, 0, 0, 0, null)]
    public void ClassifyStudentDay_counts_one_student_not_each_period(
        int present, int late, int absent, int leave, string? expected)
    {
        PeriodAttendanceMath.ClassifyStudentDay(present, late, absent, leave).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 0, 1, true)]
    [InlineData(1, 0, 2, true)]  // 50%
    [InlineData(0, 1, 2, true)]  // late counts
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 0, 0, null)]
    public void PresentTodayBadge_threshold(int present, int late, int marked, bool? expected)
    {
        PeriodAttendanceMath.PresentTodayBadge(present, late, marked).Should().Be(expected);
    }
}
