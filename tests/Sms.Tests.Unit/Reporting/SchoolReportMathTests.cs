using System.IO.Compression;
using FluentAssertions;
using Sms.Application.Services.Reporting;

namespace Sms.Tests.Unit.Reporting;

public class SchoolReportMathTests
{
    [Fact]
    public void Daily_window_is_yesterday()
    {
        var (from, to) = SchoolReportMath.Window("daily", new DateTime(2026, 11, 10, 8, 0, 0));
        from.Should().Be(new DateOnly(2026, 11, 9));
        to.Should().Be(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public void Weekly_window_is_the_previous_seven_days()
    {
        var (from, to) = SchoolReportMath.Window("weekly", new DateTime(2026, 11, 10));
        from.Should().Be(new DateOnly(2026, 11, 3));
        to.Should().Be(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public void Fifteen_day_window_is_the_previous_fifteen_days()
    {
        var (from, to) = SchoolReportMath.Window("15days", new DateTime(2026, 11, 10));
        from.Should().Be(new DateOnly(2026, 10, 26));
        to.Should().Be(new DateOnly(2026, 11, 9));
    }

    [Fact]
    public void Next_15_day_run_is_later_today_when_the_send_time_has_not_passed()
    {
        var after = new DateTime(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc);
        var next = SchoolReportMath.NextRunUtc("15days", new TimeOnly(8, 0), 330, null, null, after);
        next.Should().Be(new DateTime(2026, 11, 9, 2, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Next_15_day_run_is_fifteen_days_later_after_the_send_time()
    {
        var after = new DateTime(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);
        var next = SchoolReportMath.NextRunUtc("15days", new TimeOnly(8, 0), 330, null, null, after);
        next.Should().Be(new DateTime(2026, 11, 24, 2, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Monthly_window_is_the_previous_calendar_month()
    {
        var (from, to) = SchoolReportMath.Window("monthly", new DateTime(2026, 11, 10));
        from.Should().Be(new DateOnly(2026, 10, 1));
        to.Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void Next_daily_run_is_later_today_when_the_send_time_has_not_passed()
    {
        var after = new DateTime(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc);
        var next = SchoolReportMath.NextRunUtc("daily", new TimeOnly(8, 0), 330, null, null, after);
        next.Should().Be(new DateTime(2026, 11, 9, 2, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Next_daily_run_moves_to_tomorrow_after_the_send_time()
    {
        var after = new DateTime(2026, 11, 9, 3, 0, 0, DateTimeKind.Utc);
        var next = SchoolReportMath.NextRunUtc("daily", new TimeOnly(8, 0), 330, null, null, after);
        next.Should().Be(new DateTime(2026, 11, 10, 2, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Summary_groups_fee_collection_by_payment_mode()
    {
        var columns = new[] { "Payment Mode", "Paid Amount" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Cash", "100.50" },
            new[] { "Cash", "50" },
            new[] { "UPI", "20" },
        };
        var (cols, body) = SchoolReportMath.Summarize("fee_collection", columns, rows);
        cols.Should().Equal("Payment Mode", "Collected");
        body.Should().HaveCount(2);
        body[0].Should().Equal("Cash", "150.5");
        body[1].Should().Equal("UPI", "20");
    }

    [Fact]
    public void Charts_follow_the_filtered_rows()
    {
        var columns = SchoolReportCatalog.Find("student")!.Columns.ToList();
        var row = columns.Select(_ => "").ToArray();
        row[4] = "Class 4";
        var other = columns.Select(_ => "").ToArray();
        other[4] = "Class 4";
        var third = columns.Select(_ => "").ToArray();
        third[4] = "Class 1";
        var charts = SchoolReportMath.Charts("student", columns, [row, other, third]);
        charts.Should().ContainSingle(c => c.Title == "Students by Class");
        charts[0].Points.Should().Contain(p => p.Label == "Class 4" && p.Value == 2);
        charts[0].Points.Should().Contain(p => p.Label == "Class 1" && p.Value == 1);
    }

    [Fact]
    public void Workbook_is_an_xlsx_with_the_school_name_and_title()
    {
        var bytes = SchoolReportWorkbook.Build(
            "Greenwood High", "Student Report", "2026-11-01", "2026-11-10", "08:00", "17:00",
            "Class: Class 4", ["Admission No.", "Student Name"], [["A1", "Rahul"]]);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        zip.GetEntry("xl/worksheets/sheet1.xml").Should().NotBeNull();
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = reader.ReadToEnd();
        xml.Should().Contain("Greenwood High");
        xml.Should().Contain("Student Report");
        xml.Should().Contain("Rahul");
        xml.Should().Contain("Class: Class 4");
    }

    [Fact]
    public void Class_attendance_rolls_student_days_into_one_row()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["A1", "Ada", "Rao", "I", "A", "1", "10", "8", "1", "0", "1", "90.0"],
            ["A2", "Ben", "Rao", "I", "A", "2", "10", "5", "5", "0", "0", "50.0"],
        ];
        var grouped = SchoolReportMath.ClassAttendance(rows);
        grouped.Should().ContainSingle();
        grouped[0].Should().Equal("I", "A", "2", "10", "13", "6", "0", "1", "70.0");
    }

    [Fact]
    public void Below_75_keeps_only_students_under_the_line()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["A1", "Ada", "Rao", "I", "A", "1", "10", "8", "1", "0", "1", "90.0"],
            ["A2", "Ben", "Rao", "I", "A", "2", "10", "5", "5", "0", "0", "50.0"],
            ["A3", "Cara", "Rao", "I", "A", "3", "10", "7", "2", "0", "1", "75.0"],
            ["A4", "Dev", "Rao", "I", "A", "4", "0", "0", "0", "0", "0", "0.0"],
        ];
        var low = SchoolReportMath.Below75(rows);
        low.Should().ContainSingle();
        low[0][0].Should().Be("A2");
    }
}
