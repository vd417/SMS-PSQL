using System.Data;
using Dapper;
using Sms.Modules.Attendance;
using Sms.Modules.Reporting.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Reporting.Data;

public sealed class ReportingRepository(IDbConnectionFactory factory, CheckInRepository checkIns) : BaseRepository(factory)
{
    public async Task<DashboardStatsResponse> GetDashboardStatsAsync(DateTime today, CancellationToken ct = default)
    {
        var row = await QueryInlineAsync<DashboardStatsResponse>(@"
SELECT
  CAST((SELECT COUNT(*) FROM ""dbo"".""Students"") AS int)                                          AS ""TotalStudents"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""Classes"") AS int)                                            AS ""TotalClasses"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""AttendanceRecords""
     WHERE ""Date"" = @today::date AND ""Status"" IN ('present', 'late')) AS int)                    AS ""AttendanceToday"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""Homework""
     WHERE ""Status"" = 'todo' AND (""DueDate"" IS NULL OR ""DueDate"" >= @today::date)) AS int)      AS ""PendingAssignments"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""ExamPapers""
     WHERE ""Status"" = 'upcoming' AND (""Date"" IS NULL OR ""Date"" >= @today::date)) AS int)        AS ""UpcomingExams""",
            new { today = today.Date }, ct);
        return row[0];
    }

    public async Task<CrmPeopleSnapshotResponse> GetCrmPeopleSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(@"
SELECT
  CAST((SELECT COUNT(*) FROM ""dbo"".""Students"") AS int) AS ""StudentCount"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""Teachers"") AS int) AS ""TeacherCount"",
  CAST((SELECT COUNT(*) FROM ""dbo"".""Staff"") AS int) AS ""StaffCount"";

SELECT
  CAST(SUM(CASE WHEN UPPER(TRIM(COALESCE(""Gender"", ''))) = 'M' THEN 1 ELSE 0 END) AS int) AS ""Boys"",
  CAST(SUM(CASE WHEN UPPER(TRIM(COALESCE(""Gender"", ''))) = 'F' THEN 1 ELSE 0 END) AS int) AS ""Girls"",
  CAST(SUM(CASE WHEN UPPER(TRIM(COALESCE(""Gender"", ''))) NOT IN ('M', 'F') THEN 1 ELSE 0 END) AS int) AS ""Unspecified""
FROM ""dbo"".""Students"";

SELECT TRIM(""Grade"") AS ""Grade"", CAST(COUNT(*) AS int) AS ""Count""
FROM ""dbo"".""Students""
WHERE ""Grade"" IS NOT NULL AND TRIM(""Grade"") <> '' AND TRIM(""Grade"") NOT IN ('—', '-')
GROUP BY TRIM(""Grade"");
", cancellationToken: ct));
        var counts = await multi.ReadSingleAsync<(int StudentCount, int TeacherCount, int StaffCount)>();
        var gender = await multi.ReadSingleAsync<(int Boys, int Girls, int Unspecified)>();
        var grades = (await multi.ReadAsync<CrmGradeCount>()).AsList();
        return new CrmPeopleSnapshotResponse(
            counts.StudentCount,
            counts.TeacherCount,
            counts.StaffCount,
            grades.Count,
            gender.Boys,
            gender.Girls,
            gender.Unspecified,
            grades);
    }

    private sealed record PunchRow(Guid UserId, string Kind, DateTime At, bool Verified);

    private sealed record RosterPersonRow(
        Guid PersonId, string Name, string? Email, string? Phone,
        string? SubjectsCsv, string? Designation, string? Role, Guid? UserId);

    private sealed record UserRow(Guid Id, string? Email, string? Phone, string? Name);

    private sealed record DayPunches(DateTime? CheckInAt, bool CheckInVerified, DateTime? CheckOutAt);

    private static bool IsKind(string kind, string expected) =>
        string.Equals(kind.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static (DateTime StartUtc, DateTime EndUtc) LocalDayBoundsUtc(DateOnly day, TimeSpan utcOffset)
    {
        var localMidnight = day.ToDateTime(TimeOnly.MinValue);
        var startUtc = localMidnight - utcOffset;
        return (startUtc, startUtc.AddDays(1));
    }

    private static Dictionary<Guid, DayPunches> BuildPunchIndex(IReadOnlyList<PunchRow> rows)
    {
        var dict = new Dictionary<Guid, DayPunches>();
        foreach (var g in rows.GroupBy(r => r.UserId))
        {
            var lastIn = g.Where(x => IsKind(x.Kind, "in")).OrderBy(x => x.At).LastOrDefault();
            var lastOut = g.Where(x => IsKind(x.Kind, "out")).OrderBy(x => x.At).LastOrDefault();
            dict[g.Key] = new DayPunches(lastIn?.At, lastIn?.Verified ?? false, lastOut?.At);
        }
        return dict;
    }

    /// Resolve login user(s) for a roster row. When UserId is set, use only that account.
    /// Email is used as a fallback when the row is not linked. Phone is intentionally excluded —
    /// roster phones are often shared or stale and would attribute one person's punch to others.
    private static IEnumerable<Guid> CandidateUserIds(RosterPersonRow person, IReadOnlyList<UserRow> users)
    {
        var ids = new HashSet<Guid>();
        if (person.UserId is { } direct)
        {
            ids.Add(direct);
            return ids;
        }

        if (string.IsNullOrWhiteSpace(person.Email))
        {
            if (!string.IsNullOrWhiteSpace(person.Name))
            {
                foreach (var u in users)
                {
                    if (!string.IsNullOrWhiteSpace(u.Name)
                        && string.Equals(person.Name.Trim(), u.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ids.Add(u.Id);
                }
            }
            return ids;
        }

        foreach (var u in users)
        {
            if (!string.IsNullOrWhiteSpace(u.Email)
                && string.Equals(person.Email.Trim(), u.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                ids.Add(u.Id);
        }
        return ids;
    }

    /// Merge latest in/out across every login user linked to this teacher/staff row.
    private static DayPunches? MergePunches(IEnumerable<Guid> userIds, Dictionary<Guid, DayPunches> index)
    {
        DateTime? inAt = null, outAt = null;
        var verified = false;
        foreach (var id in userIds)
        {
            if (!index.TryGetValue(id, out var p)) continue;
            if (p.CheckInAt is { } ci && (inAt is null || ci > inAt))
            {
                inAt = ci;
                verified = p.CheckInVerified;
            }
            if (p.CheckOutAt is { } co && (outAt is null || co > outAt))
                outAt = co;
        }
        if (inAt is null && outAt is null) return null;
        return new DayPunches(inAt, verified, outAt);
    }

    private async Task<IReadOnlyList<PrincipalStaffEntry>> LoadStaffAsync(
        DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var punches = await QueryInlineAsync<PunchRow>(@"
SELECT ""UserId"", ""Kind"", ""At"", ""Verified""
FROM ""dbo"".""CheckIns""
WHERE ""At"" >= @startUtc AND ""At"" < @endUtc
  AND LOWER(TRIM(""Kind"")) IN ('in', 'out')
ORDER BY ""At""",
            new { startUtc, endUtc }, ct);
        var punchIndex = BuildPunchIndex(punches);
        var users = await QueryInlineAsync<UserRow>(
            "SELECT \"Id\", \"Email\", \"Phone\", \"Name\" FROM \"dbo\".\"Users\"", null, ct);

        var teacherRows = await QueryInlineAsync<RosterPersonRow>(@"
SELECT t.""Id"" AS ""PersonId"", t.""Name"", t.""Email"",
       COALESCE(NULLIF(TRIM(t.""Phone""), ''), NULLIF(TRIM(u.""Phone""), '')) AS ""Phone"",
       t.""SubjectsCsv"", t.""Designation"",
       CAST(NULL AS text) AS ""Role"", t.""UserId""
FROM ""dbo"".""Teachers"" t
LEFT JOIN ""dbo"".""Users"" u ON u.""Id"" = t.""UserId""
WHERE t.""Status"" = 'active'
ORDER BY t.""Name""", null, ct);

        var supportRows = await QueryInlineAsync<RosterPersonRow>(@"
SELECT s.""Id"" AS ""PersonId"", s.""Name"", s.""Email"",
       COALESCE(NULLIF(TRIM(s.""Phone""), ''), NULLIF(TRIM(u.""Phone""), '')) AS ""Phone"",
       CAST(NULL AS text) AS ""SubjectsCsv"", CAST(NULL AS text) AS ""Designation"",
       s.""Role"", s.""UserId""
FROM ""dbo"".""Staff"" s
LEFT JOIN ""dbo"".""Users"" u ON u.""Id"" = s.""UserId""
WHERE s.""Status"" = 'active'
ORDER BY s.""Name""", null, ct);

        static PrincipalStaffEntry ToEntry(
            RosterPersonRow r, IReadOnlyList<UserRow> users, Dictionary<Guid, DayPunches> index, bool isTeacher)
        {
            var punches = MergePunches(CandidateUserIds(r, users), index);
            var checkInAt = punches?.CheckInAt;
            var checkOutAt = punches?.CheckOutAt;
            return new PrincipalStaffEntry(
                r.PersonId, r.Name, Initials(r.Name),
                isTeacher && !string.IsNullOrEmpty(r.SubjectsCsv) ? r.SubjectsCsv.Split(',')[0] : null,
                r.Phone,
                checkInAt is not null,
                checkInAt,
                checkOutAt,
                checkInAt is not null && (punches?.CheckInVerified ?? false),
                isTeacher ? null : (string.IsNullOrEmpty(r.Role) ? null : r.Role),
                isTeacher ? (string.IsNullOrEmpty(r.Designation) ? "Teacher" : r.Designation) : null);
        }

        var teachers = teacherRows.Select(r => ToEntry(r, users, punchIndex, isTeacher: true)).ToList();
        var support = supportRows.Select(r => ToEntry(r, users, punchIndex, isTeacher: false)).ToList();
        return teachers.Concat(support).OrderBy(s => s.Name).ToList();
    }

    /// School-wide student attendance for principal KPIs from PeriodAttendanceRecords:
    /// (present + late) / marked periods. Unmarked days return zeros (CRM shows Not marked).
    /// Legacy daily AttendanceRecords are excluded.
    private async Task<(int PresentTotal, int StudentTotal)> ResolveStudentTotalsAsync(
        DateTime d, CancellationToken ct)
    {
        var from = d;
        var toExclusive = d.AddDays(1);
        var rows = await QueryInlineAsync<StudentTotalsRow>(@"
SELECT
  CAST(COALESCE((SELECT COUNT(*) FROM ""dbo"".""PeriodAttendanceRecords""
          WHERE ""Date"" >= @from::date AND ""Date"" < @toExclusive::date AND ""Status"" IN ('present', 'late')), 0) AS int) AS ""PresentTotal"",
  CAST(COALESCE((SELECT COUNT(*) FROM ""dbo"".""PeriodAttendanceRecords""
          WHERE ""Date"" >= @from::date AND ""Date"" < @toExclusive::date), 0) AS int) AS ""StudentTotal""",
            new { from, toExclusive }, ct);

        var row = rows.Count > 0 ? rows[0] : new StudentTotalsRow(0, 0);
        return (row.PresentTotal, row.StudentTotal);
    }

    // Parameter order must match SELECT column order for Dapper constructor mapping.
    private sealed record StudentTotalsRow(int PresentTotal, int StudentTotal);

    public async Task<PrincipalOverviewResponse> GetPrincipalOverviewAsync(
        DateOnly day, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        var (startUtc, endUtc) = LocalDayBoundsUtc(day, utcOffset);
        var staff = await LoadStaffAsync(startUtc, endUtc, ct);
        var staffPresent = staff.Count(s => s.CheckedIn);

        var (presentTotal, studentTotal) = await ResolveStudentTotalsAsync(d, ct);
        var studentsPct = studentTotal > 0
            ? Math.Round(100m * presentTotal / studentTotal, 1, MidpointRounding.AwayFromZero)
            : 0m;

        var pendingRows = await QueryInlineAsync<int>(
            "SELECT CAST(COUNT(*) AS int) FROM \"dbo\".\"LeaveRequests\" WHERE \"Status\" = 'pending'", null, ct);
        var pendingApprovals = pendingRows.Count > 0 ? pendingRows[0] : 0;

        var kpis = new PrincipalKpis(studentsPct, staffPresent, staff.Count, pendingApprovals);
        return new PrincipalOverviewResponse(kpis, staff);
    }

    public async Task<PrincipalAttendanceResponse> GetPrincipalAttendanceAsync(
        DateOnly day, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        var (startUtc, endUtc) = LocalDayBoundsUtc(day, utcOffset);
        var classes = await QueryInlineAsync<PrincipalClassAttendance>(@"
SELECT c.""Id"" AS ""ClassId"", c.""Name"" AS ""ClassName"",
       CAST(COALESCE(a.""Present"", 0) AS int) AS ""Present"",
       CAST(COALESCE(a.""Marked"", 0) AS int) AS ""Total"",
       CAST(CASE
         WHEN COALESCE(a.""Marked"", 0) > 0
         THEN ROUND(100.0 * COALESCE(a.""Present"", 0) / a.""Marked"", 1)
         ELSE 0 END AS decimal(5,1)) AS ""Pct"",
       CAST(COALESCE(a.""Marked"", 0) AS int) AS ""Marked""
FROM ""dbo"".""Classes"" c
LEFT JOIN LATERAL (
  SELECT
    SUM(CASE WHEN par.""Status"" IN ('present', 'late') THEN 1 ELSE 0 END) AS ""Present"",
    COUNT(*) AS ""Marked""
  FROM ""dbo"".""PeriodAttendanceRecords"" par
  WHERE par.""ClassId"" = c.""Id"" AND par.""Date"" >= @from::date AND par.""Date"" < @toExclusive::date
) a ON true
ORDER BY c.""Name""", new { from = d, toExclusive = d.AddDays(1) }, ct);

        var (presentTotal, studentTotal) = await ResolveStudentTotalsAsync(d, ct);
        decimal overall = studentTotal > 0
            ? Math.Round(100m * presentTotal / studentTotal, 1, MidpointRounding.AwayFromZero)
            : 0m;
        var staff = await LoadStaffAsync(startUtc, endUtc, ct);
        return new PrincipalAttendanceResponse(d, presentTotal, studentTotal, overall, classes, staff);
    }

    /// One staff/teacher person's check-in/out history across days, for the principal's staff
    /// attendance drill-down. Looks the person up in Teachers first, then Staff (RLS already
    /// scopes both to the caller's tenant, so a personId from another school resolves to no
    /// rows — same "not found" behavior as a missing id).
    public async Task<IReadOnlyList<TeacherAttendanceDayResponse>> GetStaffAttendanceHistoryAsync(
        Guid personId, int limit, TimeSpan utcOffset, CancellationToken ct = default)
    {
        var users = await QueryInlineAsync<UserRow>(
            "SELECT \"Id\", \"Email\", \"Phone\", \"Name\" FROM \"dbo\".\"Users\"", null, ct);

        var teacherRow = (await QueryInlineAsync<RosterPersonRow>(@"
SELECT t.""Id"" AS ""PersonId"", t.""Name"", t.""Email"",
       CAST(NULL AS text) AS ""Phone"", CAST(NULL AS text) AS ""SubjectsCsv"",
       CAST(NULL AS text) AS ""Designation"", CAST(NULL AS text) AS ""Role"", t.""UserId""
FROM ""dbo"".""Teachers"" t WHERE t.""Id"" = @personId", new { personId }, ct)).FirstOrDefault();

        var person = teacherRow ?? (await QueryInlineAsync<RosterPersonRow>(@"
SELECT s.""Id"" AS ""PersonId"", s.""Name"", s.""Email"",
       CAST(NULL AS text) AS ""Phone"", CAST(NULL AS text) AS ""SubjectsCsv"",
       CAST(NULL AS text) AS ""Designation"", CAST(NULL AS text) AS ""Role"", s.""UserId""
FROM ""dbo"".""Staff"" s WHERE s.""Id"" = @personId", new { personId }, ct)).FirstOrDefault();

        if (person is null) return Array.Empty<TeacherAttendanceDayResponse>();

        var userIds = CandidateUserIds(person, users).ToList();
        return await checkIns.GetHistoryAsync(userIds, limit, utcOffset, ct);
    }

    private static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        return parts.Length == 1 ? parts[0][..1].ToUpperInvariant()
            : (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
    }
}
