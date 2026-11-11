using System.Globalization;
using Sms.Modules.Reporting.Contracts;

namespace Sms.Application.Services.Reporting;

public static class SchoolReportMath
{
    public static int ClampOffset(int? minutes) => Math.Clamp(minutes ?? 0, -14 * 60, 14 * 60);

    public static (DateOnly From, DateOnly To) Window(string frequency, DateTime localNow)
    {
        var today = DateOnly.FromDateTime(localNow);
        return frequency switch
        {
            "weekly" => (today.AddDays(-7), today.AddDays(-1)),
            "15days" => (today.AddDays(-15), today.AddDays(-1)),
            "monthly" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1),
                new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
            _ => (today.AddDays(-1), today.AddDays(-1)),
        };
    }

    /// Next send instant, strictly after <paramref name="afterUtc"/>. Send time is local.
    public static DateTime NextRunUtc(
        string frequency, TimeOnly sendTime, int offsetMinutes, int? dayOfWeek, int? dayOfMonth, DateTime afterUtc)
    {
        var offset = TimeSpan.FromMinutes(ClampOffset(offsetMinutes));
        var local = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc).Add(offset);
        DateTime candidate;
        if (frequency == "weekly")
        {
            var want = Math.Clamp(dayOfWeek ?? (int)local.DayOfWeek, 0, 6);
            var date = local.Date;
            var guard = 0;
            while ((int)date.DayOfWeek != want && guard++ < 8) date = date.AddDays(1);
            candidate = date.Add(sendTime.ToTimeSpan());
            if (candidate <= local) candidate = candidate.AddDays(7);
        }
        else if (frequency == "15days")
        {
            candidate = local.Date.Add(sendTime.ToTimeSpan());
            if (candidate <= local) candidate = candidate.AddDays(15);
        }
        else if (frequency == "monthly")
        {
            var dom = Math.Clamp(dayOfMonth ?? local.Day, 1, 28);
            var date = new DateTime(local.Year, local.Month, dom);
            candidate = date.Add(sendTime.ToTimeSpan());
            if (candidate <= local)
            {
                var next = date.AddMonths(1);
                candidate = new DateTime(next.Year, next.Month, dom).Add(sendTime.ToTimeSpan());
            }
        }
        else
        {
            candidate = local.Date.Add(sendTime.ToTimeSpan());
            if (candidate <= local) candidate = candidate.AddDays(1);
        }
        return DateTime.SpecifyKind(candidate - offset, DateTimeKind.Utc);
    }

    public static string? Like(string? value)
    {
        var t = value?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return "%" + t.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    public static string? Exact(string? value)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    public static string FilterLabel(SchoolReportFilters? f)
    {
        if (f is null) return "None";
        var parts = new List<string>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) parts.Add($"{label}: {value.Trim()}");
        }
        Add("Class", f.ClassName);
        Add("Section", f.Section);
        Add("Student", f.Student);
        Add("Teacher", f.Teacher);
        Add("Employee", f.Employee);
        Add("Department", f.Department);
        Add("Designation", f.Designation);
        Add("Role", f.Role);
        Add("Gender", f.Gender);
        Add("Fee head", f.FeeHead);
        Add("Payment mode", f.PaymentMode);
        Add("Payment status", f.PaymentStatus);
        Add("Attendance", f.AttendanceStatus);
        Add("Route", f.Route);
        Add("Vehicle", f.Vehicle);
        Add("Subject", f.Subject);
        Add("Period", f.Period);
        Add("Session", f.Session);
        return parts.Count == 0 ? "None" : string.Join(" · ", parts);
    }

    public static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) Summarize(
        string key, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        return key switch
        {
            "student" => CountAndSum(columns, rows, "Class", "Paid Fee", "Pending Fee", "Class", "Students", "Paid Fee", "Pending Fee"),
            "staff" or "admin" => CountBy(columns, rows, "Role", "Role", "People"),
            "teacher" => CountBy(columns, rows, "Department", "Department", "Teachers"),
            "fee_collection" => SumBy(columns, rows, "Payment Mode", "Paid Amount", "Payment Mode", "Collected"),
            "fee_defaulters" => SumBy(columns, rows, "Class", "Outstanding Amount", "Class", "Outstanding"),
            "student_attendance" or "staff_attendance" => CountBy(columns, rows, "Attendance Status", "Attendance Status", "Records"),
            "parent" => CountBy(columns, rows, "Relationship", "Relationship", "Parents"),
            "transport" => CountBy(columns, rows, "Route Name", "Route", "Students"),
            "academic" => AverageBy(columns, rows, "Subject", "Percentage", "Subject", "Papers", "Average %"),
            _ => (columns, rows),
        };
    }

    public static IReadOnlyList<SchoolReportCard> Cards(
        string key, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var cards = new List<SchoolReportCard> { new("Rows", rows.Count.ToString(CultureInfo.InvariantCulture)) };
        void Sum(string header, string label)
        {
            var i = Index(columns, header);
            if (i < 0) return;
            var total = rows.Sum(r => Num(Cell(r, i)));
            cards.Add(new(label, total.ToString("0.##", CultureInfo.InvariantCulture)));
        }
        switch (key)
        {
            case "student":
                Sum("Paid Fee", "Paid");
                Sum("Pending Fee", "Pending");
                break;
            case "fee_collection":
                Sum("Paid Amount", "Collected");
                Sum("Pending Amount", "Pending");
                break;
            case "fee_defaulters":
                Sum("Outstanding Amount", "Outstanding");
                break;
            case "student_attendance":
            case "student_attendance_history":
            case "staff_attendance":
                var status = Index(columns, "Attendance Status");
                if (status >= 0)
                {
                    var present = rows.Count(r => IsMark(Cell(r, status), "P", "present"));
                    var pending = rows.Count(r => IsMark(Cell(r, status), "N", "pending"));
                    cards.Add(new("Present", present.ToString(CultureInfo.InvariantCulture)));
                    cards.Add(new("Pending", pending.ToString(CultureInfo.InvariantCulture)));
                }
                break;
            case "student_attendance_summary":
            case "student_attendance_low":
            case "student_attendance_class":
                var attendance = Index(columns, "Attendance Percentage");
                if (attendance >= 0 && rows.Count > 0)
                {
                    var avg = rows.Average(r => (double)Num(Cell(r, attendance)));
                    cards.Add(new("Average %", avg.ToString("0.0", CultureInfo.InvariantCulture)));
                }
                break;
            case "academic":
                var pct = Index(columns, "Percentage");
                if (pct >= 0 && rows.Count > 0)
                {
                    var avg = rows.Average(r => (double)Num(Cell(r, pct)));
                    cards.Add(new("Average %", avg.ToString("0.#", CultureInfo.InvariantCulture)));
                }
                break;
        }
        return cards;
    }

    public static IReadOnlyList<SchoolReportChart> Charts(
        string key, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var charts = new List<SchoolReportChart>();
        void AddCount(string title, string header, string kind = "rank")
        {
            var pts = Top(CountPoints(columns, rows, header), kind);
            if (pts.Count > 0) charts.Add(new(title, kind, pts));
        }
        switch (key)
        {
            case "student":
            case "parent":
                AddCount("Students by Class", "Class");
                break;
            case "staff":
            case "admin":
                AddCount("Staff by Role", "Role");
                break;
            case "teacher":
                AddCount("Staff by Role", "Department");
                break;
            case "fee_collection":
                charts.Add(new("Fee Collected vs Pending", "rank",
                [
                    new("Collected", SumOf(columns, rows, "Paid Amount")),
                    new("Pending", SumOf(columns, rows, "Pending Amount")),
                ]));
                var months = MonthPoints(columns, rows, "Payment Date", "Paid Amount");
                if (months.Count > 0) charts.Add(new("Monthly Fee Collection", "month", months));
                break;
            case "fee_defaulters":
                charts.Add(new("Fee Collected vs Pending", "rank",
                [
                    new("Paid", SumOf(columns, rows, "Total Paid")),
                    new("Pending", SumOf(columns, rows, "Outstanding Amount")),
                ]));
                break;
            case "student_attendance":
            case "student_attendance_history":
                AddCount("Student Attendance", "Attendance Status");
                break;
            case "student_attendance_summary":
            case "student_attendance_low":
            case "student_attendance_streak":
            case "student_attendance_class":
                AddCount("Students by Class", "Class");
                break;
            case "staff_attendance":
                AddCount("Staff Attendance", "Attendance Status");
                break;
            case "transport":
                AddCount("Students by Route", "Route Name");
                break;
            case "academic":
                AddCount("Papers by Subject", "Subject");
                break;
        }
        return charts;
    }

    /// <summary>One row per class and section from the monthly summary shape.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ClassAttendance(IReadOnlyList<IReadOnlyList<string>> rows) =>
        rows
            .GroupBy(r => (Class: Cell(r, 3).Trim(), Section: Cell(r, 4).Trim()))
            .OrderBy(g => g.Key.Class, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Section, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var present = g.Sum(r => Num(Cell(r, 7)));
                var absent = g.Sum(r => Num(Cell(r, 8)));
                var leave = g.Sum(r => Num(Cell(r, 9)));
                var late = g.Sum(r => Num(Cell(r, 10)));
                var marked = present + absent + leave + late;
                var pct = marked == 0 ? 0 : (present + late) / marked * 100m;
                return (IReadOnlyList<string>)new List<string>
                {
                    g.Key.Class,
                    g.Key.Section,
                    g.Count().ToString(CultureInfo.InvariantCulture),
                    g.Max(r => Num(Cell(r, 6))).ToString("0.##", CultureInfo.InvariantCulture),
                    present.ToString("0.##", CultureInfo.InvariantCulture),
                    absent.ToString("0.##", CultureInfo.InvariantCulture),
                    leave.ToString("0.##", CultureInfo.InvariantCulture),
                    late.ToString("0.##", CultureInfo.InvariantCulture),
                    pct.ToString("0.0", CultureInfo.InvariantCulture),
                };
            })
            .ToList();

    public static decimal Amount(IReadOnlyList<string> row, int index) => Num(Cell(row, index));

    public static IReadOnlyList<IReadOnlyList<string>> Below75(IReadOnlyList<IReadOnlyList<string>> rows) =>
        rows.Where(r => Num(Cell(r, 6)) > 0 && Num(Cell(r, 11)) < 75m).ToList();

    /// <summary>Drops a column when every row is blank, so subject and period stay off daily-only marks.</summary>
    public static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) OmitIfBlank(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, params string[] headers)
    {
        if (rows.Count == 0 || headers.Length == 0) return (columns, rows);
        var drop = new HashSet<int>();
        foreach (var header in headers)
        {
            var i = Index(columns, header);
            if (i >= 0 && rows.All(r => string.IsNullOrWhiteSpace(Cell(r, i)))) drop.Add(i);
        }
        if (drop.Count == 0) return (columns, rows);
        var keep = Enumerable.Range(0, columns.Count).Where(i => !drop.Contains(i)).ToList();
        return (
            keep.Select(i => columns[i]).ToList(),
            rows.Select(r => (IReadOnlyList<string>)keep.Select(i => Cell(r, i)).ToList()).ToList());
    }

    public static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) KeepColumns(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string>? keep)
    {
        if (keep is not { Count: > 0 }) return (columns, rows);
        var indexes = columns
            .Select((name, i) => (name, i))
            .Where(x => keep.Any(k => string.Equals(k, x.name, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.i)
            .ToList();
        if (indexes.Count == 0) return (columns, rows);
        var cols = indexes.Select(i => columns[i]).ToList();
        var body = rows.Select(r => (IReadOnlyList<string>)indexes.Select(i => Cell(r, i)).ToList()).ToList();
        return (cols, body);
    }

    private static (IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<string>>) CountBy(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows,
        string source, string label, string countLabel)
    {
        var i = Index(columns, source);
        var groups = rows.GroupBy(r => Blank(Cell(r, i))).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        return ([label, countLabel], groups.Select(g => (IReadOnlyList<string>)new List<string> { g.Key, g.Count().ToString(CultureInfo.InvariantCulture) }).ToList());
    }

    private static (IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<string>>) SumBy(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows,
        string source, string value, string label, string sumLabel)
    {
        var s = Index(columns, source);
        var v = Index(columns, value);
        var groups = rows.GroupBy(r => Blank(Cell(r, s))).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        return ([label, sumLabel], groups.Select(g => (IReadOnlyList<string>)new List<string>
        {
            g.Key, g.Sum(r => Num(Cell(r, v))).ToString("0.##", CultureInfo.InvariantCulture),
        }).ToList());
    }

    private static (IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<string>>) CountAndSum(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows,
        string source, string paid, string pending, string a, string b, string c, string d)
    {
        var s = Index(columns, source);
        var p = Index(columns, paid);
        var n = Index(columns, pending);
        var groups = rows.GroupBy(r => Blank(Cell(r, s))).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        return ([a, b, c, d], groups.Select(g => (IReadOnlyList<string>)new List<string>
        {
            g.Key,
            g.Count().ToString(CultureInfo.InvariantCulture),
            g.Sum(r => Num(Cell(r, p))).ToString("0.##", CultureInfo.InvariantCulture),
            g.Sum(r => Num(Cell(r, n))).ToString("0.##", CultureInfo.InvariantCulture),
        }).ToList());
    }

    private static (IReadOnlyList<string>, IReadOnlyList<IReadOnlyList<string>>) AverageBy(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows,
        string source, string value, string a, string b, string c)
    {
        var s = Index(columns, source);
        var v = Index(columns, value);
        var groups = rows.GroupBy(r => Blank(Cell(r, s))).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        return ([a, b, c], groups.Select(g => (IReadOnlyList<string>)new List<string>
        {
            g.Key,
            g.Count().ToString(CultureInfo.InvariantCulture),
            (g.Average(r => (double)Num(Cell(r, v)))).ToString("0.#", CultureInfo.InvariantCulture),
        }).ToList());
    }

    private static List<SchoolReportPoint> CountPoints(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, string header)
    {
        var i = Index(columns, header);
        if (i < 0) return [];
        return rows.GroupBy(r => Blank(Cell(r, i)))
            .Select(g => new SchoolReportPoint(g.Key, g.Count()))
            .ToList();
    }

    private static List<SchoolReportPoint> MonthPoints(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, string dateHeader, string valueHeader)
    {
        var d = Index(columns, dateHeader);
        var v = Index(columns, valueHeader);
        if (d < 0 || v < 0) return [];
        return rows
            .Select(r => (Month: Cell(r, d).Length >= 7 ? Cell(r, d)[..7] : "", Amount: Num(Cell(r, v))))
            .Where(x => x.Month.Length == 7)
            .GroupBy(x => x.Month)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SchoolReportPoint(g.Key, g.Sum(x => x.Amount)))
            .ToList();
    }

    private static List<SchoolReportPoint> Top(List<SchoolReportPoint> points, string kind) =>
        kind == "month"
            ? points
            : points.OrderByDescending(p => p.Value).Take(12).ToList();

    private static decimal SumOf(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, string header)
    {
        var i = Index(columns, header);
        return i < 0 ? 0 : rows.Sum(r => Num(Cell(r, i)));
    }

    private static int Index(IReadOnlyList<string> columns, string header)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], header, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static bool IsMark(string cell, string letter, string word) =>
        string.Equals(cell, letter, StringComparison.OrdinalIgnoreCase)
        || cell.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] ?? "" : "";

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static decimal Num(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
