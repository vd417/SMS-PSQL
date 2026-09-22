using FluentAssertions;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Academics.Data;

namespace Sms.Tests.Unit.Academics;

public class PeriodAttendanceAggregateTests
{
    [Fact]
    public void Class_day_uses_timetable_expected_periods_and_official_percentage()
    {
        var classId = Guid.NewGuid();

        var command = PeriodAttendanceAggregateSql.BuildClassDay(
            classId, new DateOnly(2026, 8, 13));
        var summary = new PeriodAttendanceClassDayRow(
            TotalStudents: 10,
            Present: 14,
            Absent: 3,
            Late: 2,
            Leave: 1,
            TotalPeriods: 3,
            MarkedPeriods: 2).ToContract();

        command.Sql.Should().Contain("FROM \"dbo\".\"TimetableSlots\" ts");
        command.Sql.Should().Contain("ts.\"ClassId\" = @ClassId");
        command.Sql.Should().Contain("s.\"Grade\" = c.\"Grade\" AND s.\"Section\" = c.\"Section\"");
        command.Sql.Should().NotContain("WHERE s.\"ClassId\"");
        command.Sql.Should().Contain("upper(left(trim(ts.\"Day\"), 3))");
        command.Parameters.Get<Guid>("ClassId").Should().Be(classId);
        command.Parameters.Get<DateTime>("Date").Should().Be(new DateTime(2026, 8, 13));
        summary.AttendancePercentage.Should().Be(80m);
        summary.PendingPeriods.Should().Be(1);
        summary.NotMarked.Should().Be(10);
    }

    [Fact]
    public void Pending_and_not_marked_are_never_negative()
    {
        var summary = new PeriodAttendanceClassDayRow(
            TotalStudents: 1,
            Present: 3,
            Absent: 0,
            Late: 0,
            Leave: 0,
            TotalPeriods: 2,
            MarkedPeriods: 3).ToContract();

        summary.PendingPeriods.Should().Be(0);
        summary.NotMarked.Should().Be(0);
    }

    [Fact]
    public void Class_day_status_buckets_only_include_expected_timetable_sessions()
    {
        var command = PeriodAttendanceAggregateSql.BuildClassDay(
            Guid.NewGuid(), new DateOnly(2026, 8, 13));

        command.Sql.Should().Contain("FROM \"ExpectedSessions\" es");
        command.Sql.Should().Contain("LEFT JOIN \"dbo\".\"PeriodAttendanceRecords\" par");
        command.Sql.Should().Contain("par.\"Period\" = es.\"Period\"");
        command.Sql.Should().Contain(
            "lower(trim(par.\"Subject\")) = es.\"Subject\"");
    }

    [Fact]
    public void Fully_unmarked_class_day_returns_zero_buckets_and_expected_not_marked()
    {
        var command = PeriodAttendanceAggregateSql.BuildClassDay(
            Guid.NewGuid(), new DateOnly(2026, 8, 13));
        var summary = new PeriodAttendanceClassDayRow(
            TotalStudents: 10,
            Present: 0,
            Absent: 0,
            Late: 0,
            Leave: 0,
            TotalPeriods: 1,
            MarkedPeriods: 0).ToContract();

        command.Sql.Should().Contain("FROM \"ExpectedSessions\" es");
        command.Sql.Should().Contain("LEFT JOIN \"dbo\".\"PeriodAttendanceRecords\" par");
        summary.Present.Should().Be(0);
        summary.Absent.Should().Be(0);
        summary.Late.Should().Be(0);
        summary.Leave.Should().Be(0);
        summary.NotMarked.Should().Be(10);
        summary.AttendancePercentage.Should().BeNull();
        summary.PendingPeriods.Should().Be(1);
    }

    [Fact]
    public void Subject_and_range_rollups_use_status_buckets_for_percentage()
    {
        var subject = new PeriodAttendanceSubjectRow(
            "Science", "Ada", 4, 3, 6, 2, 2, 2).ToContract();
        var range = new PeriodAttendanceRangeRow(6, 2, 2, 1).ToContract();

        subject.AttendancePercentage.Should().Be(66.67m);
        subject.Pending.Should().Be(1);
        range.TotalMarkedPeriods.Should().Be(11);
        range.AttendancePercentage.Should().Be(72.73m);
    }

    [Fact]
    public void Teacher_pending_is_expected_minus_marked()
    {
        var row = new PeriodAttendanceTeacherRow(
            Guid.NewGuid(), "Grace", 2, 2, 3, 7, 5, 3, 1, 1, 0).ToContract();

        row.PendingPeriods.Should().Be(2);
    }

    [Fact]
    public void Teacher_summary_counts_distinct_grades_as_classes_and_class_rows_as_sections()
    {
        var command = PeriodAttendanceAggregateSql.BuildTeachers(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 13));

        command.Sql.Should().Contain("c.\"Grade\"");
        command.Sql.Should().Contain(
            "COUNT(DISTINCT nullif(trim(es.\"Grade\"), '')) AS int) AS \"Classes\"");
        command.Sql.Should().Contain("COUNT(DISTINCT es.\"ClassId\") AS int) AS \"Sections\"");
    }

    [Fact]
    public void Range_builder_maps_all_supported_filters()
    {
        var classId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();

        var command = PeriodAttendanceAggregateSql.BuildRange(
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 8, 13),
            classId,
            "10",
            "A",
            studentId,
            " Science ",
            teacherId);

        command.Sql.Should().Contain("par.\"ClassId\" = @ClassId");
        command.Sql.Should().Contain("c.\"Grade\" = @Grade");
        command.Sql.Should().Contain("c.\"Section\" = @Section");
        command.Sql.Should().Contain("par.\"StudentId\" = @StudentId");
        command.Sql.Should().Contain("ts.\"TeacherId\" = @TeacherId");
        command.Parameters.Get<Guid?>("ClassId").Should().Be(classId);
        command.Parameters.Get<Guid?>("StudentId").Should().Be(studentId);
        command.Parameters.Get<Guid?>("TeacherId").Should().Be(teacherId);
        command.Parameters.Get<string?>("Subject").Should().Be("Science");
    }

    [Fact]
    public void Repository_exposes_phase_two_aggregate_methods()
    {
        typeof(PeriodAttendanceQueryRepository).GetMethod(
            nameof(PeriodAttendanceQueryRepository.SummarizeClassDayAsync),
            [typeof(Guid), typeof(DateOnly), typeof(CancellationToken)])
            .Should().NotBeNull();
        typeof(PeriodAttendanceQueryRepository).GetMethod(
            nameof(PeriodAttendanceQueryRepository.SummarizeSubjectsAsync),
            [typeof(Guid), typeof(DateOnly), typeof(DateOnly), typeof(CancellationToken)])
            .Should().NotBeNull();
        typeof(PeriodAttendanceQueryRepository).GetMethod(
            nameof(PeriodAttendanceQueryRepository.SummarizeTeachersAsync),
            [typeof(DateOnly), typeof(DateOnly), typeof(CancellationToken)])
            .Should().NotBeNull();
        typeof(PeriodAttendanceQueryRepository).GetMethod(
            nameof(PeriodAttendanceQueryRepository.SummarizeRangeAsync),
            [
                typeof(DateOnly), typeof(DateOnly), typeof(Guid?), typeof(string),
                typeof(string), typeof(Guid?), typeof(string), typeof(Guid?),
                typeof(CancellationToken)
            ])
            .Should().NotBeNull();
    }
}
