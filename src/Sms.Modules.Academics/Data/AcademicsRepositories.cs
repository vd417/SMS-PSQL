using System.Text.Json;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class ClassRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols = "\"Id\", \"TenantId\", \"Name\", \"Grade\", \"Section\", \"Subject\", \"Room\", \"StudentCount\", \"ClassTeacherId\"";

    // Live count via LEFT JOIN LATERAL, falling back to the stored (stubbed) column only if the
    // fallback-count query itself returns NULL (no matching Students rows at all, vs. a
    // genuine zero). Matches Students to a class the same way ReportingRepository does:
    // Grade+Section when both are set, else the free-text ClassLabel/Name. Also derives
    // NextPeriod from the next upcoming TimetableSlot for the class today.
    //
    // Known limitation: StartTime is compared as an "HH:mm" string against a UTC-formatted
    // current time (matching TimetableSlots.StartTime's existing varchar(10) shape) - if
    // the school's local timezone differs from UTC, "next period" could be off. Timezone
    // handling for timetables is a pre-existing condition across this module, not introduced
    // by this fix.
    private const string ClassSelectWithLiveCountAndNextPeriod = @"
SELECT c.""Id"", c.""TenantId"", c.""Name"", c.""Grade"", c.""Section"", c.""Subject"", c.""Room"",
       CASE WHEN sc.""Cnt"" IS NOT NULL THEN sc.""Cnt"" ELSE c.""StudentCount"" END AS ""StudentCount"",
       c.""ClassTeacherId"", np.""Subject"" AS ""NextPeriod""
FROM ""dbo"".""Classes"" c
LEFT JOIN LATERAL (
    SELECT CAST(COUNT(*) AS int) AS ""Cnt"" FROM ""dbo"".""Students"" s
    WHERE s.""Status"" = 'active'
      AND (
        (c.""Grade"" IS NOT NULL AND c.""Section"" IS NOT NULL AND s.""Grade"" = c.""Grade"" AND s.""Section"" = c.""Section"")
        OR (c.""Name"" IS NOT NULL AND s.""ClassLabel"" = c.""Name"")
      )
) sc ON true
LEFT JOIN LATERAL (
    SELECT ts.""Subject""
    FROM ""dbo"".""TimetableSlots"" ts
    WHERE ts.""ClassId"" = c.""Id""
      AND upper(left(trim(ts.""Day""), 3)) = upper(left(to_char(now() AT TIME ZONE 'UTC', 'DY'), 3))
      AND ts.""StartTime"" > to_char(now() AT TIME ZONE 'UTC', 'HH24:MI')
    ORDER BY ts.""Period""
    LIMIT 1
) np ON true";

    public Task<ClassResponse?> CreateAsync(Guid tenantId, CreateClassRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClassResponse>("dbo.Class_Create",
            new { TenantId = tenantId, r.Name, r.Grade, r.Section, r.Subject, r.Room, r.ClassTeacherId }, ct);

    public Task<ClassResponse?> UpdateAsync(Guid id, Guid tenantId, UpdateClassRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<ClassResponse>("dbo.Class_Update",
            new
            {
                Id = id,
                TenantId = tenantId,
                r.Name,
                r.Grade,
                r.Section,
                r.Subject,
                r.Room,
                r.ClassTeacherId,
                r.ClearClassTeacher,
            }, ct);

    public async Task<ClassResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<ClassResponse>($"{ClassSelectWithLiveCountAndNextPeriod} WHERE c.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public async Task<Guid?> TeacherIdForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Teachers\" WHERE \"UserId\" = @userId LIMIT 1", new { userId }, ct))
        .FirstOrDefault();

    public async Task<string?> TeacherNameAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<string?>(
            "SELECT \"Name\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<ClassResponse>> ListAsync(CancellationToken ct = default) =>
        QueryInlineAsync<ClassResponse>($"{ClassSelectWithLiveCountAndNextPeriod} ORDER BY c.\"Name\"", null, ct);

    /// Classes this teacher is associated with: they're the class-teacher, or they have any
    /// published timetable slot for it (directly assigned, or via the subject's default
    /// teacher) — the same linkage TimetableRepository.ListForTeacherAsync already uses.
    public Task<IReadOnlyList<ClassResponse>> ListForTeacherAsync(Guid teacherUserId, CancellationToken ct = default) =>
        QueryInlineAsync<ClassResponse>(@"
SELECT DISTINCT c.""Id"", c.""TenantId"", c.""Name"", c.""Grade"", c.""Section"", c.""Subject"", c.""Room"",
       CASE WHEN sc.""Cnt"" IS NOT NULL THEN sc.""Cnt"" ELSE c.""StudentCount"" END AS ""StudentCount"",
       c.""ClassTeacherId"", np.""Subject"" AS ""NextPeriod""
FROM ""dbo"".""Classes"" c
JOIN ""dbo"".""Teachers"" t ON t.""UserId"" = @teacherUserId
LEFT JOIN ""dbo"".""TimetableSlots"" ts ON ts.""ClassId"" = c.""Id""
LEFT JOIN ""dbo"".""Subjects"" sub ON sub.""Name"" = ts.""Subject""
LEFT JOIN LATERAL (
    SELECT CAST(COUNT(*) AS int) AS ""Cnt"" FROM ""dbo"".""Students"" s
    WHERE s.""Status"" = 'active'
      AND (
        (c.""Grade"" IS NOT NULL AND c.""Section"" IS NOT NULL AND s.""Grade"" = c.""Grade"" AND s.""Section"" = c.""Section"")
        OR (c.""Name"" IS NOT NULL AND s.""ClassLabel"" = c.""Name"")
      )
) sc ON true
LEFT JOIN LATERAL (
    SELECT ts2.""Subject""
    FROM ""dbo"".""TimetableSlots"" ts2
    WHERE ts2.""ClassId"" = c.""Id""
      AND upper(left(trim(ts2.""Day""), 3)) = upper(left(to_char(now() AT TIME ZONE 'UTC', 'DY'), 3))
      AND ts2.""StartTime"" > to_char(now() AT TIME ZONE 'UTC', 'HH24:MI')
    ORDER BY ts2.""Period""
    LIMIT 1
) np ON true
WHERE c.""ClassTeacherId"" = t.""Id"" OR ts.""TeacherId"" = t.""Id"" OR sub.""TeacherId"" = t.""Id""
ORDER BY c.""Name""", new { teacherUserId }, ct);
}

public sealed class SubjectRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols = "\"Id\", \"TenantId\", \"Name\", \"Short\", \"TeacherId\", \"Color\"";

    public Task<SubjectResponse?> CreateAsync(Guid tenantId, CreateSubjectRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SubjectResponse>("dbo.Subject_Create",
            new { TenantId = tenantId, r.Name, r.Short, r.TeacherId, r.Color }, ct);

    public Task<SubjectResponse?> UpdateAsync(Guid id, Guid tenantId, UpdateSubjectRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<SubjectResponse>("dbo.Subject_Update",
            new
            {
                Id = id,
                TenantId = tenantId,
                r.Name,
                r.Short,
                r.TeacherId,
                r.Color,
                r.ClearTeacher,
            }, ct);

    public async Task<SubjectResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<SubjectResponse>($"SELECT {Cols} FROM \"dbo\".\"Subjects\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<SubjectResponse>> ListAsync(CancellationToken ct = default) =>
        QueryInlineAsync<SubjectResponse>($@"
SELECT s.""Id"", s.""TenantId"", s.""Name"", s.""Short"", s.""TeacherId"", s.""Color"", t.""Name"" AS ""TeacherName""
FROM ""dbo"".""Subjects"" s
LEFT JOIN ""dbo"".""Teachers"" t ON t.""Id"" = s.""TeacherId""
ORDER BY s.""Name""", null, ct);

    public Task<int> DeleteAsync(Guid id, Guid tenantId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.Subject_Delete", new { Id = id, TenantId = tenantId }, ct);
}

public sealed record ClassSubjectNameRow(Guid ClassId, string Name);

public sealed class ClassSubjectRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<IReadOnlyList<string>> ListNamesAsync(Guid classId, CancellationToken ct = default) =>
        QueryProcAsync<string>("dbo.ClassSubject_List", new { ClassId = classId }, ct);

    public Task<IReadOnlyList<string>> ReplaceAsync(
        Guid tenantId, Guid classId, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(
            names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        return QueryProcAsync<string>("dbo.ClassSubject_Replace",
            new { TenantId = tenantId, ClassId = classId, NamesJson = json }, ct);
    }

    public Task<IReadOnlyList<ClassSubjectNameRow>> ListForClassesAsync(
        IReadOnlyCollection<Guid> classIds, CancellationToken ct = default)
    {
        if (classIds.Count == 0)
            return Task.FromResult<IReadOnlyList<ClassSubjectNameRow>>([]);
        return QueryInlineAsync<ClassSubjectNameRow>(
            "SELECT \"ClassId\", \"Name\" FROM \"dbo\".\"ClassSubjects\" WHERE \"ClassId\" = ANY(@classIds) ORDER BY \"Name\"",
            new { classIds = classIds.ToArray() }, ct);
    }
}

public sealed class AttendanceRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    /// Bulk roll-call upsert: rows arrive as a jsonb-serialized array (no 1:1 Postgres mapping
    /// for the old AttendanceTvp table-valued parameter — see dbo.attendance_bulkupsert).
    public Task BulkUpsertAsync(Guid tenantId, Guid classId, DateTime date, Guid? markedBy,
        IReadOnlyList<AttendanceUpsertRow> rows, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(rows.Select(r => new { r.StudentId, r.Status }));
        return ExecuteProcAsync("dbo.Attendance_BulkUpsert",
            new { TenantId = tenantId, ClassId = classId, Date = date.Date, MarkedBy = markedBy, Rows = json }, ct);
    }

    public Task<IReadOnlyList<AttendanceRecordResponse>> ListAsync(
        Guid classId, DateTime date, CancellationToken ct = default) =>
        QueryInlineAsync<AttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"ClassId\", \"StudentId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"AttendanceRecords\" " +
            "WHERE \"ClassId\" = @classId AND \"Date\" = @date ORDER BY \"StudentId\"",
            new { classId, date = date.Date }, ct);

    public Task<IReadOnlyList<AttendanceRecordResponse>> ListRangeAsync(
        Guid classId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryInlineAsync<AttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"ClassId\", \"StudentId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"AttendanceRecords\" " +
            "WHERE \"ClassId\" = @classId AND \"Date\" BETWEEN @from AND @to ORDER BY \"Date\", \"StudentId\"",
            new { classId, from = from.Date, to = to.Date }, ct);

    /// Every day-mark for the current tenant since a date (RLS-scoped). Used to compute absence streaks.
    public Task<IReadOnlyList<AttendanceMarkRow>> ListSinceAsync(DateTime since, CancellationToken ct = default) =>
        QueryInlineAsync<AttendanceMarkRow>(
            "SELECT \"StudentId\", \"Date\", \"Status\" FROM \"dbo\".\"AttendanceRecords\" " +
            "WHERE \"Date\" >= @since ORDER BY \"StudentId\", \"Date\"",
            new { since = since.Date }, ct);

    public Task<IReadOnlyList<AttendanceRecordResponse>> ListForStudentAsync(
        Guid studentId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryInlineAsync<AttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"ClassId\", \"StudentId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"AttendanceRecords\" " +
            "WHERE \"StudentId\" = @studentId AND \"Date\" BETWEEN @from AND @to ORDER BY \"Date\"",
            new { studentId, from = from.Date, to = to.Date }, ct);
}

/// Admin/principal marking a teacher or staff member's daily attendance — distinct from
/// Sms.Modules.Attendance, which is self check-in/out punches for the logged-in user.
public sealed class StaffAttendanceRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task BulkUpsertAsync(Guid tenantId, string personType, DateTime date, Guid? markedBy,
        IReadOnlyList<StaffAttendanceUpsertRow> rows, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(rows.Select(r => new { r.PersonId, r.Status }));
        return ExecuteProcAsync("dbo.StaffAttendance_BulkUpsert",
            new { TenantId = tenantId, PersonType = personType, Date = date.Date, MarkedBy = markedBy, Rows = json }, ct);
    }

    public Task<IReadOnlyList<StaffAttendanceRecordResponse>> ListAsync(
        string personType, DateTime date, CancellationToken ct = default) =>
        QueryInlineAsync<StaffAttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"PersonType\", \"PersonId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"StaffAttendanceRecords\" " +
            "WHERE \"PersonType\" = @personType AND \"Date\" = @date ORDER BY \"PersonId\"",
            new { personType, date = date.Date }, ct);

    public Task<IReadOnlyList<StaffAttendanceRecordResponse>> ListRangeAsync(
        string personType, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryInlineAsync<StaffAttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"PersonType\", \"PersonId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"StaffAttendanceRecords\" " +
            "WHERE \"PersonType\" = @personType AND \"Date\" BETWEEN @from AND @to ORDER BY \"Date\", \"PersonId\"",
            new { personType, from = from.Date, to = to.Date }, ct);

    public Task<IReadOnlyList<StaffAttendanceRecordResponse>> ListForPersonAsync(
        string personType, Guid personId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryInlineAsync<StaffAttendanceRecordResponse>(
            "SELECT \"Id\", \"TenantId\", \"PersonType\", \"PersonId\", \"Date\", \"Status\", \"MarkedBy\" FROM \"dbo\".\"StaffAttendanceRecords\" " +
            "WHERE \"PersonType\" = @personType AND \"PersonId\" = @personId AND \"Date\" BETWEEN @from AND @to ORDER BY \"Date\"",
            new { personType, personId, from = from.Date, to = to.Date }, ct);
}

public sealed class SchoolHouseRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default) =>
        QueryProcAsync<string>("dbo.SchoolHouse_List", new { }, ct);

    public Task<IReadOnlyList<string>> ReplaceAsync(
        Guid tenantId, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var cleaned = names
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(cleaned);
        return QueryProcAsync<string>("dbo.SchoolHouse_Replace", new
        {
            TenantId = tenantId,
            NamesJson = json,
        }, ct);
    }
}

public sealed class PeriodAttendanceRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task BulkUpsertAsync(
        Guid tenantId, Guid classId, DateTime date, int period, string subject,
        Guid? periodId, Guid? subjectId, Guid? markedBy, string? markedByRole,
        IReadOnlyList<AttendanceUpsertRow> rows, CancellationToken ct = default,
        string? geoFenceStatus = null, int? geoDistanceMeters = null, DateTime? geoCapturedAt = null)
    {
        var json = JsonSerializer.Serialize(rows.Select(r => new { r.StudentId, r.Status }));
        return ExecuteProcAsync("dbo.PeriodAttendance_BulkUpsert", new
        {
            TenantId = tenantId,
            ClassId = classId,
            Date = date.Date,
            Period = period,
            PeriodId = periodId,
            Subject = subject,
            SubjectId = subjectId,
            MarkedBy = markedBy,
            MarkedByRole = markedByRole,
            GeoFenceStatus = geoFenceStatus,
            GeoDistanceMeters = geoDistanceMeters,
            GeoCapturedAt = geoCapturedAt,
            Rows = json,
        }, ct);
    }

    public Task<IReadOnlyList<PeriodAttendanceRecordResponse>> ListAsync(
        Guid classId, DateTime date, int period, string subject, CancellationToken ct = default) =>
        QueryInlineAsync<PeriodAttendanceRecordResponse>(@"
SELECT ""Id"", ""TenantId"", ""ClassId"", ""StudentId"", ""Date"", ""Period"", ""PeriodId"", ""Subject"", ""SubjectId"", ""Status"", ""MarkedBy"", ""MarkedByRole""
FROM ""dbo"".""PeriodAttendanceRecords""
WHERE ""ClassId"" = @classId AND ""Date"" = @date AND ""Period"" = @period AND ""Subject"" = @subject
ORDER BY ""StudentId""",
            new { classId, date = date.Date, period, subject }, ct);

    public Task<IReadOnlyList<PeriodAttendanceRecordResponse>> ListForClassDayAsync(
        Guid classId, DateTime date, CancellationToken ct = default) =>
        QueryInlineAsync<PeriodAttendanceRecordResponse>(@"
SELECT ""Id"", ""TenantId"", ""ClassId"", ""StudentId"", ""Date"", ""Period"", ""PeriodId"", ""Subject"", ""SubjectId"", ""Status"", ""MarkedBy"", ""MarkedByRole""
FROM ""dbo"".""PeriodAttendanceRecords""
WHERE ""ClassId"" = @classId AND ""Date"" = @date
ORDER BY ""Period"", ""Subject"", ""StudentId""",
            new { classId, date = date.Date }, ct);

    public Task<IReadOnlyList<PeriodAttendanceRecordResponse>> ListForStudentAsync(
        Guid studentId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryInlineAsync<PeriodAttendanceRecordResponse>(@"
SELECT ""Id"", ""TenantId"", ""ClassId"", ""StudentId"", ""Date"", ""Period"", ""PeriodId"", ""Subject"", ""SubjectId"", ""Status"", ""MarkedBy"", ""MarkedByRole""
FROM ""dbo"".""PeriodAttendanceRecords""
WHERE ""StudentId"" = @studentId AND ""Date"" BETWEEN @from AND @to
ORDER BY ""Date"", ""Period"", ""Subject""",
            new { studentId, from = from.Date, to = to.Date }, ct);

    public async Task<PeriodAttendanceSummaryResponse> SummarizeForStudentAsync(
        Guid studentId, DateTime from, DateTime to, DateTime today, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<AggRow>(@"
SELECT
  CAST(COUNT(*) AS int) AS ""Marked"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'present' THEN 1 ELSE 0 END), 0) AS int) AS ""Present"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'late' THEN 1 ELSE 0 END), 0) AS int) AS ""Late"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'absent' THEN 1 ELSE 0 END), 0) AS int) AS ""Absent"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'leave' THEN 1 ELSE 0 END), 0) AS int) AS ""LeaveCnt"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today AND ""Status"" = 'present' THEN 1 ELSE 0 END), 0) AS int) AS ""TodayPresent"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today AND ""Status"" = 'late' THEN 1 ELSE 0 END), 0) AS int) AS ""TodayLate"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today THEN 1 ELSE 0 END), 0) AS int) AS ""TodayMarked""
FROM ""dbo"".""PeriodAttendanceRecords""
WHERE ""StudentId"" = @studentId AND ""Date"" BETWEEN @from AND @to",
            new { studentId, from = from.Date, to = to.Date, today = today.Date }, ct);

        var r = rows.Count > 0 ? rows[0] : new AggRow(0, 0, 0, 0, 0, 0, 0, 0);
        var counts = PeriodAttendanceMath.FromStatusBuckets(r.Present, r.Late, r.Absent, r.LeaveCnt);
        return new PeriodAttendanceSummaryResponse(
            counts.TotalMarkedPeriods,
            counts.PresentPeriods,
            counts.LatePeriods,
            counts.AbsentPeriods,
            counts.LeavePeriods,
            counts.AttendancePercentage,
            PeriodAttendanceMath.PresentTodayBadge(r.TodayPresent, r.TodayLate, r.TodayMarked));
    }

    public async Task<PeriodAttendanceSummaryResponse> SummarizeForClassAsync(
        Guid classId, DateTime from, DateTime to, DateTime today, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<AggRow>(@"
SELECT
  CAST(COUNT(*) AS int) AS ""Marked"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'present' THEN 1 ELSE 0 END), 0) AS int) AS ""Present"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'late' THEN 1 ELSE 0 END), 0) AS int) AS ""Late"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'absent' THEN 1 ELSE 0 END), 0) AS int) AS ""Absent"",
  CAST(COALESCE(SUM(CASE WHEN ""Status"" = 'leave' THEN 1 ELSE 0 END), 0) AS int) AS ""LeaveCnt"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today AND ""Status"" = 'present' THEN 1 ELSE 0 END), 0) AS int) AS ""TodayPresent"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today AND ""Status"" = 'late' THEN 1 ELSE 0 END), 0) AS int) AS ""TodayLate"",
  CAST(COALESCE(SUM(CASE WHEN ""Date"" = @today THEN 1 ELSE 0 END), 0) AS int) AS ""TodayMarked""
FROM ""dbo"".""PeriodAttendanceRecords""
WHERE ""ClassId"" = @classId AND ""Date"" BETWEEN @from AND @to",
            new { classId, from = from.Date, to = to.Date, today = today.Date }, ct);

        var r = rows.Count > 0 ? rows[0] : new AggRow(0, 0, 0, 0, 0, 0, 0, 0);
        var counts = PeriodAttendanceMath.FromStatusBuckets(r.Present, r.Late, r.Absent, r.LeaveCnt);
        return new PeriodAttendanceSummaryResponse(
            counts.TotalMarkedPeriods,
            counts.PresentPeriods,
            counts.LatePeriods,
            counts.AbsentPeriods,
            counts.LeavePeriods,
            counts.AttendancePercentage,
            PeriodAttendanceMath.PresentTodayBadge(r.TodayPresent, r.TodayLate, r.TodayMarked));
    }

    private sealed record AggRow(
        int Marked, int Present, int Late, int Absent, int LeaveCnt,
        int TodayPresent, int TodayLate, int TodayMarked);
}
