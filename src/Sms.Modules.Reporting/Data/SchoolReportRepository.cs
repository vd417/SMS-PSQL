using System.Data;
using Dapper;
using Sms.Modules.Reporting.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Reporting.Data;

public sealed class SchoolReportArgs
{
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public string? FromTime { get; init; }
    public string? ToTime { get; init; }
    public int OffsetMinutes { get; init; }
    public string? ClassName { get; init; }
    public string? Section { get; init; }
    public string? Student { get; init; }
    public string? Teacher { get; init; }
    public string? Employee { get; init; }
    public string? Department { get; init; }
    public string? Designation { get; init; }
    public string? Role { get; init; }
    public string? Gender { get; init; }
    public string? FeeHead { get; init; }
    public string? PaymentMode { get; init; }
    public string? PaymentStatus { get; init; }
    public string? AttendanceStatus { get; init; }
    public string? Route { get; init; }
    public string? Vehicle { get; init; }
    public string? Subject { get; init; }
    public string? Period { get; init; }
}

public sealed class SessionBounds
{
    public DateTime? StartsOn { get; set; }
    public DateTime? EndsOn { get; set; }
}

public sealed class SchoolReportRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<string> SchoolNameAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<string>(
            """SELECT "Name" FROM "dbo"."Tenants" WHERE "Id" = @tenantId""",
            new { tenantId }, ct);
        return rows.FirstOrDefault() ?? "School";
    }

    public async Task<SessionBounds?> SessionBoundsAsync(string name, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<SessionBounds>(
            """
            SELECT "StartsOn", "EndsOn"
            FROM "dbo"."AcademicSessions"
            WHERE LOWER("Name") = LOWER(@name)
            ORDER BY "IsCurrent" DESC, "StartsOn" DESC NULLS LAST
            LIMIT 1
            """,
            new { name }, ct);
        return rows.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string[]>> RunAsync(string key, SchoolReportArgs a, CancellationToken ct)
    {
        var sql = key switch
        {
            "student" => StudentSql,
            "staff" => StaffSql,
            "teacher" => TeacherSql,
            "admin" => AdminSql,
            "fee_collection" => FeeCollectionSql,
            "fee_defaulters" => FeeDefaultersSql,
            "student_attendance" => StudentAttendanceSql,
            "student_attendance_summary" => StudentAttendanceSummarySql,
            "student_attendance_streak" => StudentAttendanceStreakSql,
            "student_attendance_daily" => StudentAttendanceDailySql,
            "student_attendance_by_subject" => StudentSubjectSql,
            "student_attendance_subject" => SubjectSummarySql,
            "student_attendance_period" => PeriodSummarySql,
            "student_attendance_marking" => TeacherMarkingSql,
            "student_attendance_corrections" => AttendanceCorrectionSql,
            "student_attendance_trend" => AttendanceTrendSql,
            "student_attendance_compare" => AttendanceCompareSql,
            "student_attendance_shift" => AttendanceShiftSql,
            "student_attendance_absence" => AbsenceAnalysisSql,
            "staff_attendance" => StaffAttendanceSql,
            "parent" => ParentSql,
            "transport" => TransportSql,
            "academic" => AcademicSql,
            _ => null,
        };
        if (sql is null) return [];
        var cols = key switch
        {
            "student" => 20,
            "staff" => 12,
            "teacher" => 13,
            "admin" => 12,
            "fee_collection" => 16,
            "fee_defaulters" => 15,
            "student_attendance" => 14,
            "student_attendance_summary" => 13,
            "student_attendance_streak" => 9,
            "student_attendance_daily" => 8,
            "student_attendance_by_subject" => 11,
            "student_attendance_subject" => 10,
            "student_attendance_period" => 10,
            "student_attendance_marking" => 9,
            "student_attendance_corrections" => 11,
            "student_attendance_trend" => 6,
            "student_attendance_compare" => 5,
            "student_attendance_shift" => 7,
            "student_attendance_absence" => 3,
            "staff_attendance" => 12,
            "parent" => 13,
            "transport" => 14,
            "academic" => 12,
            _ => 0,
        };
        return await ReadGrid(sql, a, cols, ct);
    }

    public async Task<SchoolReportOptions> OptionsAsync(CancellationToken ct)
    {
        await using var conn = await Factory.OpenAsync(ct);
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition(OptionsSql, cancellationToken: ct));
        static async Task<IReadOnlyList<string>> Next(SqlMapper.GridReader grid)
        {
            var rows = await grid.ReadAsync<string>();
            return rows.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
        }
        return new SchoolReportOptions(
            await Next(multi), await Next(multi), await Next(multi), await Next(multi), await Next(multi),
            await Next(multi), await Next(multi), await Next(multi), await Next(multi), await Next(multi),
            await Next(multi));
    }

    public async Task<IReadOnlyList<SchoolReportDue>> ListDueAsync(DateTime now, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<SchoolReportDue>(
            """
            SELECT "Id", "TenantId" FROM "dbo"."ReportSchedules"
            WHERE "Status" = 'active' AND "NextRunAt" <= @now
            ORDER BY "NextRunAt"
            LIMIT 20
            """,
            new { now }, ct);
        return rows;
    }

    public Task<int> ClaimAsync(Guid id, DateTime now, DateTime next, CancellationToken ct) =>
        ExecuteCountAsync(
            """
            UPDATE "dbo"."ReportSchedules"
            SET "NextRunAt" = @next, "LastRunAt" = @now, "UpdatedAt" = now()
            WHERE "Id" = @id AND "Status" = 'active' AND "NextRunAt" <= @now
            """,
            new { id, now, next }, ct);

    public async Task<IReadOnlyList<SchoolReportSchedule>> ListSchedulesAsync(string? reportKey, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<ScheduleRow>(
            """
            SELECT "Id", "ReportKey", "Title", "Frequency", "SendTime", "DayOfWeek", "DayOfMonth",
                   "UtcOffsetMinutes", "Format", "Recipients", "Subject", "Message", "FiltersJson",
                   "Status", "NextRunAt", "LastRunAt"
            FROM "dbo"."ReportSchedules"
            WHERE @reportKey IS NULL OR "ReportKey" = @reportKey
            ORDER BY "CreatedAt" DESC
            """,
            new { reportKey }, ct);
        return rows.Select(ToSchedule).ToList();
    }

    public async Task<SchoolReportSchedule?> GetScheduleAsync(Guid id, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<ScheduleRow>(
            """
            SELECT "Id", "ReportKey", "Title", "Frequency", "SendTime", "DayOfWeek", "DayOfMonth",
                   "UtcOffsetMinutes", "Format", "Recipients", "Subject", "Message", "FiltersJson",
                   "Status", "NextRunAt", "LastRunAt"
            FROM "dbo"."ReportSchedules" WHERE "Id" = @id
            """,
            new { id }, ct);
        var row = rows.FirstOrDefault();
        return row is null ? null : ToSchedule(row);
    }

    public async Task<Guid> InsertScheduleAsync(
        Guid tenantId, Guid? createdBy, SaveReportScheduleRequest req, string title, string recipients,
        string filtersJson, DateTime nextRun, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "dbo"."ReportSchedules"
                ("Id", "TenantId", "ReportKey", "Title", "Frequency", "SendTime", "DayOfWeek", "DayOfMonth",
                 "UtcOffsetMinutes", "Format", "Recipients", "Subject", "Message", "FiltersJson", "Status",
                 "NextRunAt", "CreatedBy")
            VALUES
                (@id, @tenantId, @ReportKey, @title, @Frequency, @sendTime::time, @DayOfWeek, @DayOfMonth,
                 @offset, @format, @recipients, @Subject, @Message, @filtersJson, 'active', @nextRun, @createdBy)
            """,
            new
            {
                id, tenantId, req.ReportKey, title, req.Frequency, sendTime = req.SendTime,
                req.DayOfWeek, req.DayOfMonth, offset = req.OffsetMinutes ?? 0,
                format = string.IsNullOrWhiteSpace(req.Format) ? "xlsx" : req.Format,
                recipients, req.Subject, req.Message, filtersJson, nextRun, createdBy,
            }, ct);
        return id;
    }

    public Task UpdateScheduleAsync(
        Guid id, SaveReportScheduleRequest req, string title, string recipients, string filtersJson,
        DateTime nextRun, CancellationToken ct) =>
        ExecuteAsync(
            """
            UPDATE "dbo"."ReportSchedules"
            SET "ReportKey" = @ReportKey, "Title" = @title, "Frequency" = @Frequency, "SendTime" = @sendTime::time,
                "DayOfWeek" = @DayOfWeek, "DayOfMonth" = @DayOfMonth, "UtcOffsetMinutes" = @offset,
                "Format" = @format, "Recipients" = @recipients, "Subject" = @Subject, "Message" = @Message,
                "FiltersJson" = @filtersJson, "NextRunAt" = @nextRun, "UpdatedAt" = now()
            WHERE "Id" = @id
            """,
            new
            {
                id, req.ReportKey, title, req.Frequency, sendTime = req.SendTime,
                req.DayOfWeek, req.DayOfMonth, offset = req.OffsetMinutes ?? 0,
                format = string.IsNullOrWhiteSpace(req.Format) ? "xlsx" : req.Format,
                recipients, req.Subject, req.Message, filtersJson, nextRun,
            }, ct);

    public Task SetStatusAsync(Guid id, string status, DateTime? nextRun, CancellationToken ct) =>
        ExecuteAsync(
            """
            UPDATE "dbo"."ReportSchedules"
            SET "Status" = @status, "NextRunAt" = COALESCE(@nextRun, "NextRunAt"), "UpdatedAt" = now()
            WHERE "Id" = @id
            """,
            new { id, status, nextRun }, ct);

    public Task InsertRunAsync(
        Guid tenantId, Guid scheduleId, string status, string? detail, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        ExecuteAsync(
            """
            INSERT INTO "dbo"."ReportScheduleRuns"
                ("TenantId", "ScheduleId", "FinishedAt", "Status", "Detail", "FromDate", "ToDate")
            VALUES (@tenantId, @scheduleId, now(), @status, @detail, @from, @to)
            """,
            new { tenantId, scheduleId, status, detail, from = from?.ToDateTime(TimeOnly.MinValue), to = to?.ToDateTime(TimeOnly.MinValue) }, ct);

    public async Task<IReadOnlyList<SchoolReportScheduleRun>> ListRunsAsync(Guid scheduleId, CancellationToken ct)
    {
        var rows = await QueryInlineAsync<SchoolReportScheduleRun>(
            """
            SELECT "Id", "ScheduleId", "StartedAt", "FinishedAt", "Status", "Detail", "FromDate", "ToDate"
            FROM "dbo"."ReportScheduleRuns"
            WHERE "ScheduleId" = @scheduleId
            ORDER BY "StartedAt" DESC
            LIMIT 30
            """,
            new { scheduleId }, ct);
        return rows;
    }

    private async Task<List<string[]>> ReadGrid(string sql, SchoolReportArgs args, int cols, CancellationToken ct)
    {
        await using var conn = await Factory.OpenAsync(ct);
        var raw = await conn.QueryAsync(new CommandDefinition(sql, args, cancellationToken: ct));
        var list = new List<string[]>();
        foreach (var row in raw)
        {
            var dict = (IDictionary<string, object>)row;
            var cells = new string[cols];
            for (var i = 0; i < cols; i++)
                cells[i] = Text(dict, "c" + i);
            list.Add(cells);
        }
        return list;
    }

    private static string Text(IDictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    break;
                }
            }
        }
        return value is null or DBNull ? "" : Convert.ToString(value) ?? "";
    }

    private async Task ExecuteAsync(string sql, object args, CancellationToken ct) =>
        await ExecuteCountAsync(sql, args, ct);

    private async Task<int> ExecuteCountAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await Factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    private static SchoolReportSchedule ToSchedule(ScheduleRow r) => new(
        r.Id, r.ReportKey, r.Title, r.Frequency, r.SendTime.ToString(@"hh\:mm"),
        r.DayOfWeek, r.DayOfMonth, r.UtcOffsetMinutes, r.Format, r.Recipients, r.Subject, r.Message,
        r.FiltersJson, r.Status, r.NextRunAt, r.LastRunAt);

    private sealed record ScheduleRow(
        Guid Id, string ReportKey, string Title, string Frequency, TimeSpan SendTime,
        short? DayOfWeek, short? DayOfMonth, int UtcOffsetMinutes, string Format,
        string Recipients, string? Subject, string? Message, string FiltersJson,
        string Status, DateTime NextRunAt, DateTime? LastRunAt);

    private const string Gender = """
        AND (@Gender IS NULL OR ((@Gender = 'O' AND UPPER(COALESCE({0}."Gender", '')) NOT IN ('M', 'F')) OR UPPER(COALESCE({0}."Gender", '')) = @Gender))
        """;

    private static string G(string alias) => string.Format(Gender, alias);

    private static readonly string StudentSql = """
        SELECT
          s."AdmissionNo" AS "c0",
          s."Name" AS "c1",
          COALESCE(to_char(s."Dob"::date, 'YYYY-MM-DD'), '') AS "c2",
          CASE UPPER(COALESCE(s."Gender", '')) WHEN 'M' THEN 'Male' WHEN 'F' THEN 'Female' ELSE '' END AS "c3",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c4",
          COALESCE(s."Section", '') AS "c5",
          CASE WHEN s."Roll" = 0 THEN '' ELSE s."Roll"::text END AS "c6",
          COALESCE(sess."Name", '') AS "c7",
          to_char((s."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD') AS "c8",
          COALESCE(s."GuardianName", '') AS "c9",
          COALESCE(s."GuardianPhone", '') AS "c10",
          COALESCE(s."GuardianEmail", '') AS "c11",
          COALESCE(s."Address", '') AS "c12",
          CASE WHEN bus."StudentId" IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM "dbo"."StudentTransportOptOut" o WHERE o."StudentId" = s."Id") THEN 'Yes' ELSE 'No' END AS "c13",
          COALESCE(bus.route, '') AS "c14",
          COALESCE(s."FeeStatus", '') AS "c15",
          to_char(COALESCE(fee.billed, 0), 'FM999999990.00') AS "c16",
          to_char(COALESCE(fee.paid, 0), 'FM999999990.00') AS "c17",
          to_char(GREATEST(COALESCE(fee.billed, 0) - COALESCE(fee.paid, 0), 0), 'FM999999990.00') AS "c18",
          COALESCE(s."Status", '') AS "c19"
        FROM "dbo"."Students" s
        LEFT JOIN LATERAL (
          SELECT "Name" FROM "dbo"."AcademicSessions" WHERE "IsCurrent" = true ORDER BY "StartsOn" DESC NULLS LAST LIMIT 1
        ) sess ON true
        LEFT JOIN LATERAL (
          SELECT sa."StudentId", COALESCE(b."RouteName", '') AS route
          FROM "dbo"."StudentBusAssignments" sa
          LEFT JOIN "dbo"."Buses" b ON b."Id" = sa."BusId"
          WHERE sa."StudentId" = s."Id"
          ORDER BY sa."CreatedAt" DESC
          LIMIT 1
        ) bus ON true
        LEFT JOIN LATERAL (
          SELECT SUM("Amount") AS billed, SUM("PaidAmount") AS paid
          FROM "dbo"."FeeInvoices" i WHERE i."StudentId" = s."Id"
        ) fee ON true
        WHERE (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
        """ + G("s") + """
        ORDER BY 5, 6, 2
        LIMIT 5000
        """;

    private static readonly string StaffSql = """
        WITH people AS (
          SELECT t."Id" AS person_id, 'teacher'::text AS kind, t."UserId" AS user_id,
                 COALESCE(t."EmployeeCode", '') AS code, t."Name" AS name, 'Teacher' AS role,
                 COALESCE(t."Designation", '') AS designation, COALESCE(t."Department", '') AS dept,
                 t."Gender" AS gender
          FROM "dbo"."Teachers" t
          UNION ALL
          SELECT st."Id", 'staff', st."UserId", COALESCE(st."EmployeeCode", ''), st."Name",
                 COALESCE(NULLIF(st."Role", ''), 'Staff'), COALESCE(st."Role", ''),
                 COALESCE(st."Department", ''), st."Gender"
          FROM "dbo"."Staff" st
        ),
        activity AS (
          SELECT p.person_id, p.kind, a."Date" AS day, a."Status" AS mark
          FROM people p
          JOIN "dbo"."StaffAttendanceRecords" a
            ON a."PersonId" = p.person_id AND a."PersonType" = p.kind
           AND a."Date" BETWEEN @FromDate::date AND @ToDate::date
          UNION
          SELECT p.person_id, p.kind, (c."At" + make_interval(mins => @OffsetMinutes))::date, NULL::varchar
          FROM people p
          JOIN "dbo"."CheckIns" c ON c."UserId" = p.user_id
          WHERE p.user_id IS NOT NULL
            AND (c."At" + make_interval(mins => @OffsetMinutes))::date BETWEEN @FromDate::date AND @ToDate::date
        ),
        spine AS (
          SELECT person_id, kind, day, MAX(mark) AS mark
          FROM activity
          GROUP BY person_id, kind, day
          UNION ALL
          SELECT p.person_id, p.kind, NULL::date, NULL::varchar
          FROM people p
          WHERE NOT EXISTS (SELECT 1 FROM activity a WHERE a.person_id = p.person_id AND a.kind = p.kind)
        )
        SELECT
          p.code AS "c0",
          p.name AS "c1",
          p.role AS "c2",
          p.dept AS "c3",
          COALESCE(to_char(s.day, 'YYYY-MM-DD'), '') AS "c4",
          COALESCE(to_char(ci.in_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c5",
          COALESCE(to_char(ci.out_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c6",
          CASE WHEN ci.in_at IS NOT NULL AND ci.out_at IS NOT NULL AND ci.out_at > ci.in_at
            THEN to_char(EXTRACT(EPOCH FROM (ci.out_at - ci.in_at)) / 3600, 'FM990.00') ELSE '' END AS "c7",
          COALESCE(slots.periods, '') AS "c8",
          COALESCE(slots.subjects, '') AS "c9",
          COALESCE(lv.kind, CASE WHEN s.mark ILIKE '%leave%' THEN s.mark ELSE '' END, '') AS "c10",
          CASE LOWER(COALESCE(s.mark, ''))
            WHEN 'present' THEN 'P' WHEN 'absent' THEN 'A' WHEN 'late' THEN 'L'
            WHEN 'half_day' THEN 'H' WHEN 'pending' THEN 'N' WHEN 'leave' THEN 'LV'
            WHEN '' THEN '' ELSE s.mark END AS "c11"
        FROM spine s
        JOIN people p ON p.person_id = s.person_id AND p.kind = s.kind
        LEFT JOIN LATERAL (
          SELECT MIN(c."At") FILTER (WHERE c."Kind" = 'in') AS in_at,
                 MAX(c."At") FILTER (WHERE c."Kind" = 'out') AS out_at
          FROM "dbo"."CheckIns" c
          WHERE p.user_id IS NOT NULL AND c."UserId" = p.user_id AND s.day IS NOT NULL
            AND (c."At" + make_interval(mins => @OffsetMinutes))::date = s.day
        ) ci ON true
        LEFT JOIN LATERAL (
          SELECT string_agg(x.period, ', ' ORDER BY x.period::int) AS periods,
                 string_agg(x.subject, ', ' ORDER BY x.period::int) AS subjects
          FROM (
            SELECT DISTINCT ts."Period"::text AS period, NULLIF(ts."Subject", '') AS subject
            FROM "dbo"."TimetableSlots" ts
            WHERE p.kind = 'teacher' AND ts."TeacherId" = p.person_id AND s.day IS NOT NULL
              AND upper(left(COALESCE(ts."Day", ''), 3)) =
                  upper((ARRAY['Sun','Mon','Tue','Wed','Thu','Fri','Sat'])[EXTRACT(DOW FROM s.day)::int + 1])
          ) x
        ) slots ON true
        LEFT JOIN LATERAL (
          SELECT lr."Type" AS kind
          FROM "dbo"."LeaveRequests" lr
          WHERE p.user_id IS NOT NULL AND lr."RequesterId" = p.user_id AND s.day IS NOT NULL
            AND lr."Status" ILIKE 'approv%'
            AND s.day BETWEEN lr."FromDate" AND COALESCE(lr."ToDate", lr."FromDate")
          ORDER BY lr."FromDate" DESC
          LIMIT 1
        ) lv ON true
        WHERE (@Employee IS NULL OR p.code ILIKE @Employee ESCAPE '\' OR p.name ILIKE @Employee ESCAPE '\')
          AND (@Department IS NULL OR LOWER(p.dept) = LOWER(@Department))
          AND (@Designation IS NULL OR LOWER(p.designation) = LOWER(@Designation))
          AND (@Role IS NULL OR LOWER(p.role) = LOWER(@Role))
          AND (@Gender IS NULL OR ((@Gender = 'O' AND UPPER(COALESCE(p.gender, '')) NOT IN ('M', 'F')) OR UPPER(COALESCE(p.gender, '')) = @Gender))
        ORDER BY p.name, s.day DESC NULLS LAST
        LIMIT 5000
        """;

    private static readonly string TeacherSql = """
        SELECT
          LEFT(t."Id"::text, 8) AS "c0",
          COALESCE(t."EmployeeCode", '') AS "c1",
          t."Name" AS "c2",
          COALESCE(t."Department", '') AS "c3",
          COALESCE(NULLIF(ts."Subject", ''), NULLIF(t."SubjectsCsv", ''), sub.names, '') AS "c4",
          COALESCE(NULLIF(cls."Name", ''), NULLIF(ts."ClassName", ''), '') AS "c5",
          COALESCE(cls."Section", '') AS "c6",
          COALESCE(ts."Period"::text, '') AS "c7",
          CASE WHEN COALESCE(ts."StartTime", '') <> '' OR COALESCE(ts."EndTime", '') <> ''
            THEN trim(both FROM concat_ws('–', NULLIF(ts."StartTime", ''), NULLIF(ts."EndTime", '')))
            ELSE '' END AS "c8",
          COALESCE(t."ClassTeacher", '') AS "c9",
          COALESCE(stu.n, 0)::text AS "c10",
          to_char((t."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD') AS "c11",
          COALESCE(t."Status", '') AS "c12"
        FROM "dbo"."Teachers" t
        LEFT JOIN "dbo"."TimetableSlots" ts ON ts."TeacherId" = t."Id"
        LEFT JOIN "dbo"."Classes" cls ON cls."Id" = ts."ClassId"
        LEFT JOIN LATERAL (
          SELECT string_agg(DISTINCT s."Name", ', ') AS names
          FROM "dbo"."Subjects" s WHERE s."TeacherId" = t."Id"
        ) sub ON true
        LEFT JOIN LATERAL (
          SELECT COUNT(*)::int AS n FROM "dbo"."Students" s
          WHERE LOWER(COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '')) =
                LOWER(COALESCE(NULLIF(cls."Name", ''), NULLIF(ts."ClassName", ''), NULLIF(t."ClassTeacher", ''), ''))
        ) stu ON true
        WHERE (@Teacher IS NULL OR t."Name" ILIKE @Teacher ESCAPE '\' OR COALESCE(t."EmployeeCode", '') ILIKE @Teacher ESCAPE '\')
          AND (@Department IS NULL OR LOWER(COALESCE(t."Department", '')) = LOWER(@Department))
        """ + G("t") + """
        ORDER BY t."Name", ts."Period"
        LIMIT 5000
        """;

    private static readonly string AdminSql = """
        SELECT * FROM (
          SELECT
            COALESCE(NULLIF(st."EmployeeCode", ''), LEFT(u."Id"::text, 8)) AS "c0",
            COALESCE(NULLIF(u."Name", ''), NULLIF(st."Name", ''), '') AS "c1",
            COALESCE(st."Role", '') AS "c2",
            COALESCE(string_agg(DISTINCT r."Role", ', '), '') AS "c3",
            COALESCE(st."Department", '') AS "c4",
            COALESCE(NULLIF(u."Phone", ''), NULLIF(st."Phone", ''), '') AS "c5",
            COALESCE(NULLIF(u."Email", ''), NULLIF(st."Email", ''), '') AS "c6",
            to_char((u."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD') AS "c7",
            '' AS "c8",
            '' AS "c9",
            COALESCE(to_char(u."LastSeenAt" + make_interval(mins => @OffsetMinutes), 'YYYY-MM-DD HH24:MI'), '') AS "c10",
            COALESCE(u."Status", st."Status", '') AS "c11"
          FROM "dbo"."Users" u
          JOIN "dbo"."UserRoles" r ON r."UserId" = u."Id"
          LEFT JOIN "dbo"."Staff" st ON st."UserId" = u."Id"
          WHERE r."Role" ~* '(admin|principal|owner)'
          GROUP BY u."Id", st."EmployeeCode", u."Name", st."Name", st."Role", st."Department",
                   u."Phone", st."Phone", u."Email", st."Email", u."CreatedAt", u."LastSeenAt", u."Status", st."Status"
        ) q
        WHERE (@Employee IS NULL OR q."c0" ILIKE @Employee ESCAPE '\' OR q."c1" ILIKE @Employee ESCAPE '\' OR q."c6" ILIKE @Employee ESCAPE '\')
          AND (@Department IS NULL OR LOWER(q."c4") = LOWER(@Department))
          AND (@Role IS NULL OR strpos(LOWER(q."c3"), LOWER(@Role)) > 0)
        ORDER BY q."c1"
        LIMIT 5000
        """;

    private static readonly string FeeCollectionSql = """
        SELECT
          UPPER(LEFT(REPLACE(p."Id"::text, '-', ''), 8)) AS "c0",
          COALESCE(s."AdmissionNo", '') AS "c1",
          COALESCE(NULLIF(p."StudentName", ''), s."Name", '') AS "c2",
          COALESCE(NULLIF(p."ClassLabel", ''), NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c3",
          COALESCE(s."Section", '') AS "c4",
          COALESCE(h."Name", NULLIF(p."FeeType", ''), '') AS "c5",
          to_char(COALESCE(i."Amount", p."Amount"), 'FM999999990.00') AS "c6",
          '' AS "c7",
          '' AS "c8",
          to_char(p."Amount", 'FM999999990.00') AS "c9",
          to_char(GREATEST(COALESCE(i."Amount", 0) - COALESCE(i."PaidAmount", 0), 0), 'FM999999990.00') AS "c10",
          COALESCE(p."Method", '') AS "c11",
          COALESCE(p."Ref", '') AS "c12",
          to_char(p."Date", 'YYYY-MM-DD') AS "c13",
          '' AS "c14",
          COALESCE(i."Status", 'paid') AS "c15"
        FROM "dbo"."FeePayments" p
        LEFT JOIN "dbo"."Students" s ON s."Id" = p."StudentId"
        LEFT JOIN "dbo"."FeeInvoices" i ON i."Id" = p."InvoiceId"
        LEFT JOIN LATERAL (
          SELECT "Name" FROM "dbo"."FeeHeads" h
          WHERE h."Id"::text = p."HeadId" OR (p."HeadId" IS NOT NULL AND h."Code" = p."HeadId")
          LIMIT 1
        ) h ON true
        WHERE p."Date" >= @FromDate::date AND p."Date" <= @ToDate::date
          AND (@FromTime IS NULL OR (p."CreatedAt" + make_interval(mins => @OffsetMinutes))::time >= @FromTime::time)
          AND (@ToTime IS NULL OR (p."CreatedAt" + make_interval(mins => @OffsetMinutes))::time <= @ToTime::time)
          AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(p."ClassLabel", ''), NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR COALESCE(p."StudentName", s."Name", '') ILIKE @Student ESCAPE '\' OR COALESCE(s."AdmissionNo", '') ILIKE @Student ESCAPE '\')
          AND (@FeeHead IS NULL OR LOWER(COALESCE(h."Name", p."FeeType", '')) = LOWER(@FeeHead))
          AND (@PaymentMode IS NULL OR LOWER(COALESCE(p."Method", '')) = LOWER(@PaymentMode))
          AND (@PaymentStatus IS NULL OR LOWER(COALESCE(i."Status", 'paid')) = LOWER(@PaymentStatus))
        ORDER BY p."Date" DESC, p."CreatedAt" DESC
        LIMIT 5000
        """;

    private static readonly string FeeDefaultersSql = """
        SELECT
          s."AdmissionNo" AS "c0",
          s."Name" AS "c1",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c2",
          COALESCE(s."Section", '') AS "c3",
          COALESCE(s."GuardianName", '') AS "c4",
          COALESCE(s."GuardianPhone", '') AS "c5",
          COALESCE(line.head, '') AS "c6",
          to_char(i."Amount", 'FM999999990.00') AS "c7",
          to_char(i."PaidAmount", 'FM999999990.00') AS "c8",
          to_char(i."Amount" - i."PaidAmount", 'FM999999990.00') AS "c9",
          COALESCE(to_char(i."DueDate", 'YYYY-MM-DD'), '') AS "c10",
          CASE WHEN i."DueDate" IS NOT NULL AND i."DueDate" < @ToDate::date THEN (@ToDate::date - i."DueDate")::text ELSE '0' END AS "c11",
          COALESCE(pay.last_on, '') AS "c12",
          '' AS "c13",
          COALESCE(i."Status", '') AS "c14"
        FROM "dbo"."FeeInvoices" i
        JOIN "dbo"."Students" s ON s."Id" = i."StudentId"
        LEFT JOIN LATERAL (
          SELECT l."FeeHeadName" AS head FROM "dbo"."FeeInvoiceLines" l
          WHERE l."InvoiceId" = i."Id" ORDER BY l."CreatedAt" LIMIT 1
        ) line ON true
        LEFT JOIN LATERAL (
          SELECT to_char(MAX(p."Date"), 'YYYY-MM-DD') AS last_on
          FROM "dbo"."FeePayments" p WHERE p."InvoiceId" = i."Id"
        ) pay ON true
        WHERE i."Amount" > i."PaidAmount"
          AND (i."DueDate" IS NULL OR i."DueDate" <= @ToDate::date)
          AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
          AND (@FeeHead IS NULL OR LOWER(COALESCE(line.head, '')) = LOWER(@FeeHead))
          AND (@PaymentStatus IS NULL OR LOWER(COALESCE(i."Status", '')) = LOWER(@PaymentStatus))
        ORDER BY (i."Amount" - i."PaidAmount") DESC
        LIMIT 5000
        """;

    private static readonly string StudentAttendanceSql = """
        SELECT * FROM (
          SELECT
            to_char(p."Date", 'YYYY-MM-DD') AS "c0",
            s."AdmissionNo" AS "c1",
            s."Name" AS "c2",
            COALESCE(s."GuardianName", '') AS "c3",
            COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c4",
            COALESCE(s."Section", '') AS "c5",
            CASE WHEN s."Roll" = 0 THEN '' ELSE s."Roll"::text END AS "c6",
            COALESCE(p."Subject", '') AS "c7",
            p."Period"::text AS "c8",
            CASE WHEN COALESCE(ts."StartTime", '') <> '' OR COALESCE(ts."EndTime", '') <> ''
              THEN trim(both FROM concat_ws('–', NULLIF(ts."StartTime", ''), NULLIF(ts."EndTime", '')))
              ELSE '' END AS "c9",
            CASE LOWER(COALESCE(p."Status", ''))
              WHEN 'present' THEN 'P' WHEN 'absent' THEN 'A' WHEN 'late' THEN 'L'
              WHEN 'half_day' THEN 'H' WHEN 'pending' THEN 'N'
              WHEN 'leave' THEN 'LV' WHEN 'on_leave' THEN 'LV'
              ELSE COALESCE(p."Status", '') END AS "c10",
            '' AS "c11",
            COALESCE(u."Name", '') AS "c12",
            COALESCE(to_char(p."UpdatedAt" + make_interval(mins => @OffsetMinutes), 'YYYY-MM-DD HH24:MI'), '') AS "c13"
          FROM "dbo"."PeriodAttendanceRecords" p
          JOIN "dbo"."Students" s ON s."Id" = p."StudentId"
          LEFT JOIN LATERAL (
            SELECT slot."StartTime", slot."EndTime"
            FROM "dbo"."TimetableSlots" slot
            WHERE slot."ClassId" = p."ClassId" AND slot."Period" = p."Period"
              AND upper(left(COALESCE(slot."Day", ''), 3)) =
                  upper((ARRAY['Sun','Mon','Tue','Wed','Thu','Fri','Sat'])[EXTRACT(DOW FROM p."Date")::int + 1])
              AND (COALESCE(slot."Subject", '') = '' OR lower(slot."Subject") = lower(p."Subject"))
            ORDER BY CASE WHEN lower(COALESCE(slot."Subject", '')) = lower(p."Subject") THEN 0 ELSE 1 END
            LIMIT 1
          ) ts ON true
          LEFT JOIN "dbo"."Users" u ON u."Id" = p."MarkedBy"
          WHERE p."Date" >= @FromDate::date AND p."Date" <= @ToDate::date
            AND EXTRACT(DOW FROM p."Date") <> 0
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."CalendarEvents" ev
              WHERE ev."Date" = p."Date" AND LOWER(COALESCE(ev."Type", '')) = 'holiday')
            AND LOWER(COALESCE(s."Status", 'active')) NOT IN ('inactive', 'removed', 'left', 'withdrawn')
            AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
            AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
            AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
            AND (@Subject IS NULL OR p."Subject" ILIKE @Subject ESCAPE '\')
            AND (@Period IS NULL OR p."Period"::text = @Period)
            AND (@AttendanceStatus IS NULL OR LOWER(p."Status") = LOWER(@AttendanceStatus)
              OR (LOWER(@AttendanceStatus) = 'leave' AND LOWER(p."Status") IN ('leave', 'on_leave')))
          UNION ALL
          SELECT
            to_char(a."Date", 'YYYY-MM-DD'),
            s."AdmissionNo",
            s."Name",
            COALESCE(s."GuardianName", ''),
            COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), ''),
            COALESCE(s."Section", ''),
            CASE WHEN s."Roll" = 0 THEN '' ELSE s."Roll"::text END,
            '',
            '',
            '',
            CASE LOWER(COALESCE(a."Status", ''))
              WHEN 'present' THEN 'P' WHEN 'absent' THEN 'A' WHEN 'late' THEN 'L'
              WHEN 'half_day' THEN 'H' WHEN 'pending' THEN 'N'
              WHEN 'leave' THEN 'LV' WHEN 'on_leave' THEN 'LV'
              ELSE COALESCE(a."Status", '') END,
            '',
            COALESCE(u."Name", ''),
            ''
          FROM "dbo"."AttendanceRecords" a
          JOIN "dbo"."Students" s ON s."Id" = a."StudentId"
          LEFT JOIN "dbo"."Users" u ON u."Id" = a."MarkedBy"
          WHERE a."Date" >= @FromDate::date AND a."Date" <= @ToDate::date
            AND EXTRACT(DOW FROM a."Date") <> 0
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."CalendarEvents" ev
              WHERE ev."Date" = a."Date" AND LOWER(COALESCE(ev."Type", '')) = 'holiday')
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."PeriodAttendanceRecords" p
              WHERE p."StudentId" = a."StudentId" AND p."Date" = a."Date")
            AND @Subject IS NULL AND @Period IS NULL
            AND LOWER(COALESCE(s."Status", 'active')) NOT IN ('inactive', 'removed', 'left', 'withdrawn')
            AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
            AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
            AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
            AND (@AttendanceStatus IS NULL OR LOWER(a."Status") = LOWER(@AttendanceStatus)
              OR (LOWER(@AttendanceStatus) = 'leave' AND LOWER(a."Status") IN ('leave', 'on_leave')))
        ) q
        ORDER BY q."c0" DESC, q."c2", q."c8"
        LIMIT 5000
        """;

    private const string StudentDayCtes = """
        days AS (
          SELECT g.day::date AS day
          FROM generate_series(@FromDate::date, @ToDate::date, interval '1 day') AS g(day)
          WHERE EXTRACT(DOW FROM g.day) <> 0
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."CalendarEvents" ev
              WHERE ev."Date" = g.day::date AND LOWER(COALESCE(ev."Type", '')) = 'holiday')
        ),
        roster AS (
          SELECT s."Id" AS id,
                 s."AdmissionNo" AS adm,
                 s."Name" AS name,
                 COALESCE(s."GuardianName", '') AS guardian,
                 COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS class_name,
                 COALESCE(s."Section", '') AS section,
                 CASE WHEN s."Roll" = 0 THEN '' ELSE s."Roll"::text END AS roll
          FROM "dbo"."Students" s
          WHERE LOWER(COALESCE(s."Status", 'active')) NOT IN ('inactive', 'removed', 'left', 'withdrawn')
            AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
            AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
            AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
        ),
        slots AS (
          SELECT lower(COALESCE(NULLIF(cls."Name", ''), NULLIF(ts."ClassName", ''), '')) AS class_name,
                 lower(COALESCE(cls."Section", '')) AS section,
                 upper(left(COALESCE(ts."Day", ''), 3)) AS dow,
                 ts."Period"::text AS period,
                 COALESCE(ts."Subject", '') AS subject
          FROM "dbo"."TimetableSlots" ts
          LEFT JOIN "dbo"."Classes" cls ON cls."Id" = ts."ClassId"
        ),
        scope AS (
          SELECT r.id, d.day
          FROM roster r
          JOIN days d ON true
          WHERE (@Subject IS NULL AND @Period IS NULL)
             OR EXISTS (
               SELECT 1 FROM slots sl
               WHERE sl.class_name = lower(r.class_name)
                 AND (sl.section = '' OR r.section = '' OR sl.section = lower(r.section))
                 AND sl.dow = upper((ARRAY['Sun','Mon','Tue','Wed','Thu','Fri','Sat'])[EXTRACT(DOW FROM d.day)::int + 1])
                 AND (@Subject IS NULL OR sl.subject ILIKE @Subject ESCAPE '\')
                 AND (@Period IS NULL OR sl.period = @Period)
             )
             OR EXISTS (
               SELECT 1 FROM "dbo"."PeriodAttendanceRecords" p
               WHERE p."StudentId" = r.id AND p."Date" = d.day
                 AND (@Subject IS NULL OR p."Subject" ILIKE @Subject ESCAPE '\')
                 AND (@Period IS NULL OR p."Period"::text = @Period)
             )
        ),
        raw_marks AS (
          SELECT p."StudentId" AS id, p."Date" AS day, LOWER(p."Status") AS st
          FROM "dbo"."PeriodAttendanceRecords" p
          WHERE p."Date" BETWEEN @FromDate::date AND @ToDate::date
            AND (@Subject IS NULL OR p."Subject" ILIKE @Subject ESCAPE '\')
            AND (@Period IS NULL OR p."Period"::text = @Period)
          UNION ALL
          SELECT a."StudentId", a."Date", LOWER(a."Status")
          FROM "dbo"."AttendanceRecords" a
          WHERE @Subject IS NULL AND @Period IS NULL
            AND a."Date" BETWEEN @FromDate::date AND @ToDate::date
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."PeriodAttendanceRecords" p
              WHERE p."StudentId" = a."StudentId" AND p."Date" = a."Date")
        ),
        day_status AS (
          SELECT id, day,
            CASE
              WHEN bool_or(st IN ('leave', 'on_leave')) THEN 'leave'
              WHEN bool_or(st = 'absent') AND NOT bool_or(st IN ('present', 'half_day', 'late')) THEN 'absent'
              WHEN bool_or(st = 'late') AND NOT bool_or(st IN ('present', 'half_day')) THEN 'late'
              WHEN bool_or(st IN ('present', 'half_day')) THEN 'present'
              ELSE 'pending'
            END AS st
          FROM raw_marks
          GROUP BY id, day
        )
        """;

    private static readonly string StudentAttendanceSummarySql = "WITH " + StudentDayCtes + """
        SELECT
          r.adm AS "c0",
          r.name AS "c1",
          r.guardian AS "c2",
          r.class_name AS "c3",
          r.section AS "c4",
          r.roll AS "c5",
          COUNT(sc.day)::text AS "c6",
          COUNT(sc.day) FILTER (WHERE ds.st = 'present')::text AS "c7",
          COUNT(sc.day) FILTER (WHERE ds.st = 'absent')::text AS "c8",
          COUNT(sc.day) FILTER (WHERE ds.st = 'leave')::text AS "c9",
          COUNT(sc.day) FILTER (WHERE ds.st = 'late')::text AS "c10",
          CASE WHEN COUNT(sc.day) FILTER (WHERE ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(
              (100.0 * COUNT(sc.day) FILTER (WHERE ds.st IN ('present', 'late')))
              / COUNT(sc.day) FILTER (WHERE ds.st IN ('present', 'absent', 'leave', 'late')),
              'FM990.0')
          END AS "c11",
          COALESCE((
            SELECT MAX(g.cnt)::text
            FROM (
              SELECT COUNT(*) AS cnt
              FROM (
                SELECT wrn - ROW_NUMBER() OVER (ORDER BY wrn) AS grp
                FROM (
                  SELECT ROW_NUMBER() OVER (ORDER BY sc2.day) AS wrn, COALESCE(ds2.st, '') AS st
                  FROM scope sc2
                  LEFT JOIN day_status ds2 ON ds2.id = sc2.id AND ds2.day = sc2.day
                  WHERE sc2.id = r.id
                ) numbered
                WHERE numbered.st = 'absent'
              ) islands
              GROUP BY islands.grp
            ) g
          ), '0') AS "c12"
        FROM roster r
        JOIN scope sc ON sc.id = r.id
        LEFT JOIN day_status ds ON ds.id = sc.id AND ds.day = sc.day
        WHERE @AttendanceStatus IS NULL
           OR EXISTS (
             SELECT 1 FROM raw_marks m
             WHERE m.id = r.id
               AND (m.st = LOWER(@AttendanceStatus)
                 OR (LOWER(@AttendanceStatus) = 'leave' AND m.st IN ('leave', 'on_leave')))
           )
        GROUP BY r.id, r.adm, r.name, r.guardian, r.class_name, r.section, r.roll
        ORDER BY r.class_name, r.section, r.name
        LIMIT 5000
        """;

    private static readonly string StudentAttendanceStreakSql = "WITH " + StudentDayCtes + """
        , numbered AS (
          SELECT sc.id, sc.day,
                 ROW_NUMBER() OVER (PARTITION BY sc.id ORDER BY sc.day) AS wrn,
                 COALESCE(ds.st, '') AS st
          FROM scope sc
          LEFT JOIN day_status ds ON ds.id = sc.id AND ds.day = sc.day
        ),
        absent AS (
          SELECT id, day, wrn FROM numbered WHERE st = 'absent'
        ),
        islands AS (
          SELECT id, day, wrn - ROW_NUMBER() OVER (PARTITION BY id ORDER BY wrn) AS grp
          FROM absent
        ),
        streaks AS (
          SELECT id, COUNT(*) AS n, MIN(day) AS from_day, MAX(day) AS to_day
          FROM islands
          GROUP BY id, grp
          HAVING COUNT(*) >= 3
        ),
        best AS (
          SELECT DISTINCT ON (id) id, n, from_day, to_day
          FROM streaks
          ORDER BY id, n DESC, to_day DESC
        )
        SELECT
          r.adm AS "c0",
          r.name AS "c1",
          r.guardian AS "c2",
          r.class_name AS "c3",
          r.section AS "c4",
          r.roll AS "c5",
          b.n::text AS "c6",
          to_char(b.from_day, 'YYYY-MM-DD') AS "c7",
          to_char(b.to_day, 'YYYY-MM-DD') AS "c8"
        FROM best b
        JOIN roster r ON r.id = b.id
        ORDER BY b.n DESC, r.class_name, r.name
        LIMIT 5000
        """;

    private static readonly string StudentAttendanceDailySql = "WITH " + StudentDayCtes + """
        SELECT
          to_char(ds.day, 'YYYY-MM-DD') AS "c0",
          r.adm AS "c1",
          r.name AS "c2",
          r.guardian AS "c3",
          r.class_name AS "c4",
          r.section AS "c5",
          r.roll AS "c6",
          CASE ds.st
            WHEN 'present' THEN 'P' WHEN 'absent' THEN 'A' WHEN 'late' THEN 'L'
            WHEN 'leave' THEN 'LV' ELSE 'N' END AS "c7"
        FROM day_status ds
        JOIN roster r ON r.id = ds.id
        WHERE (@AttendanceStatus IS NULL OR ds.st = LOWER(@AttendanceStatus)
          OR (LOWER(@AttendanceStatus) = 'half_day' AND ds.st = 'present')
          OR (LOWER(@AttendanceStatus) = 'leave' AND ds.st = 'leave'))
        ORDER BY ds.day DESC, r.class_name, r.name
        LIMIT 5000
        """;

    private const string PeriodMarkCtes = """
        days AS (
          SELECT g.day::date AS day
          FROM generate_series(@FromDate::date, @ToDate::date, interval '1 day') AS g(day)
          WHERE EXTRACT(DOW FROM g.day) <> 0
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."CalendarEvents" ev
              WHERE ev."Date" = g.day::date AND LOWER(COALESCE(ev."Type", '')) = 'holiday')
        ),
        roster AS (
          SELECT s."Id" AS id,
                 s."AdmissionNo" AS adm,
                 s."Name" AS name,
                 COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS class_name,
                 COALESCE(s."Section", '') AS section
          FROM "dbo"."Students" s
          WHERE LOWER(COALESCE(s."Status", 'active')) NOT IN ('inactive', 'removed', 'left', 'withdrawn')
            AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
            AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
            AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
        ),
        marks AS (
          SELECT p."StudentId" AS id, p."Date" AS day, p."Period" AS period,
                 COALESCE(p."Subject", '') AS subject, LOWER(COALESCE(p."Status", '')) AS st,
                 p."ClassId" AS class_id, p."UpdatedAt" AS marked_at, p."MarkedBy" AS marked_by
          FROM "dbo"."PeriodAttendanceRecords" p
          JOIN days d ON d.day = p."Date"
          WHERE (@Subject IS NULL OR p."Subject" ILIKE @Subject ESCAPE '\')
            AND (@Period IS NULL OR p."Period"::text = @Period)
        )
        """;

    private static readonly string StudentSubjectSql = "WITH " + PeriodMarkCtes + """
        SELECT
          r.adm AS "c0",
          r.name AS "c1",
          r.class_name AS "c2",
          r.section AS "c3",
          m.subject AS "c4",
          COALESCE(MAX((
            SELECT t."Name" FROM "dbo"."TimetableSlots" ts
            JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
            WHERE ts."ClassId" = m.class_id AND ts."Period" = m.period
              AND lower(COALESCE(ts."Subject", '')) = lower(m.subject)
            LIMIT 1
          )), '') AS "c5",
          COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day'))::text AS "c6",
          COUNT(*) FILTER (WHERE m.st = 'absent')::text AS "c7",
          COUNT(*) FILTER (WHERE m.st IN ('leave', 'on_leave'))::text AS "c8",
          COUNT(*) FILTER (WHERE m.st = 'late')::text AS "c9",
          CASE WHEN COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(
              100.0 * COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'late'))
              / COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')),
              'FM990.0') END AS "c10"
        FROM marks m
        JOIN roster r ON r.id = m.id
        WHERE @Teacher IS NULL OR EXISTS (
          SELECT 1 FROM "dbo"."TimetableSlots" ts
          JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
          WHERE ts."ClassId" = m.class_id AND ts."Period" = m.period
            AND lower(COALESCE(ts."Subject", '')) = lower(m.subject)
            AND t."Name" ILIKE @Teacher ESCAPE '\'
        )
        GROUP BY r.id, r.adm, r.name, r.class_name, r.section, m.subject
        ORDER BY r.class_name, r.section, r.name, m.subject
        LIMIT 5000
        """;

    private static readonly string SubjectSummarySql = "WITH " + PeriodMarkCtes + """
        SELECT
          m.subject AS "c0",
          r.class_name AS "c1",
          r.section AS "c2",
          COALESCE(MAX((
            SELECT t."Name" FROM "dbo"."TimetableSlots" ts
            JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
            WHERE ts."ClassId" = m.class_id AND ts."Period" = m.period
              AND lower(COALESCE(ts."Subject", '')) = lower(m.subject)
            LIMIT 1
          )), '') AS "c3",
          COUNT(DISTINCT r.id)::text AS "c4",
          COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day'))::text AS "c5",
          COUNT(*) FILTER (WHERE m.st = 'absent')::text AS "c6",
          COUNT(*) FILTER (WHERE m.st IN ('leave', 'on_leave'))::text AS "c7",
          COUNT(*) FILTER (WHERE m.st = 'late')::text AS "c8",
          CASE WHEN COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(
              100.0 * COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'late'))
              / COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')),
              'FM990.0') END AS "c9"
        FROM marks m
        JOIN roster r ON r.id = m.id
        WHERE @Teacher IS NULL OR EXISTS (
          SELECT 1 FROM "dbo"."TimetableSlots" ts
          JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
          WHERE ts."ClassId" = m.class_id AND ts."Period" = m.period
            AND lower(COALESCE(ts."Subject", '')) = lower(m.subject)
            AND t."Name" ILIKE @Teacher ESCAPE '\'
        )
        GROUP BY m.subject, r.class_name, r.section
        ORDER BY r.class_name, r.section, m.subject
        LIMIT 5000
        """;

    private static readonly string PeriodSummarySql = "WITH " + PeriodMarkCtes + """
        SELECT
          m.period::text AS "c0",
          m.subject AS "c1",
          r.class_name AS "c2",
          r.section AS "c3",
          COUNT(DISTINCT r.id)::text AS "c4",
          COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day'))::text AS "c5",
          COUNT(*) FILTER (WHERE m.st = 'absent')::text AS "c6",
          COUNT(*) FILTER (WHERE m.st IN ('leave', 'on_leave'))::text AS "c7",
          COUNT(*) FILTER (WHERE m.st = 'late')::text AS "c8",
          CASE WHEN COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(
              100.0 * COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'late'))
              / COUNT(*) FILTER (WHERE m.st IN ('present', 'half_day', 'absent', 'leave', 'on_leave', 'late')),
              'FM990.0') END AS "c9"
        FROM marks m
        JOIN roster r ON r.id = m.id
        WHERE @Teacher IS NULL OR EXISTS (
          SELECT 1 FROM "dbo"."TimetableSlots" ts
          JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
          WHERE ts."ClassId" = m.class_id AND ts."Period" = m.period
            AND lower(COALESCE(ts."Subject", '')) = lower(m.subject)
            AND t."Name" ILIKE @Teacher ESCAPE '\'
        )
        GROUP BY m.period, m.subject, r.class_name, r.section
        ORDER BY r.class_name, r.section, m.period, m.subject
        LIMIT 5000
        """;

    private static readonly string TeacherMarkingSql = """
        WITH days AS (
          SELECT g.day::date AS day
          FROM generate_series(@FromDate::date, @ToDate::date, interval '1 day') AS g(day)
          WHERE EXTRACT(DOW FROM g.day) <> 0
            AND NOT EXISTS (
              SELECT 1 FROM "dbo"."CalendarEvents" ev
              WHERE ev."Date" = g.day::date AND LOWER(COALESCE(ev."Type", '')) = 'holiday')
        ),
        slot AS (
          SELECT COALESCE(t."Name", 'Unassigned') AS teacher,
                 COALESCE(NULLIF(cls."Name", ''), NULLIF(ts."ClassName", ''), '') AS class_name,
                 COALESCE(cls."Section", '') AS section,
                 COALESCE(ts."Subject", '') AS subject,
                 ts."Period" AS period,
                 ts."ClassId" AS class_id,
                 upper(left(COALESCE(ts."Day", ''), 3)) AS dow
          FROM "dbo"."TimetableSlots" ts
          LEFT JOIN "dbo"."Classes" cls ON cls."Id" = ts."ClassId"
          LEFT JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
        ),
        scheduled AS (
          SELECT sl.teacher, sl.class_name, sl.section, sl.subject, sl.period, sl.class_id, d.day
          FROM slot sl
          JOIN days d
            ON sl.dow = upper((ARRAY['Sun','Mon','Tue','Wed','Thu','Fri','Sat'])[EXTRACT(DOW FROM d.day)::int + 1])
          WHERE (@ClassName IS NULL OR LOWER(sl.class_name) = LOWER(@ClassName))
            AND (@Section IS NULL OR LOWER(sl.section) = LOWER(@Section))
            AND (@Subject IS NULL OR sl.subject ILIKE @Subject ESCAPE '\')
            AND (@Period IS NULL OR sl.period::text = @Period)
            AND (@Teacher IS NULL OR sl.teacher ILIKE @Teacher ESCAPE '\')
        )
        SELECT
          s.teacher AS "c0",
          s.class_name AS "c1",
          s.section AS "c2",
          s.subject AS "c3",
          COUNT(*)::text AS "c4",
          COUNT(*) FILTER (WHERE COALESCE(m.n, 0) > 0)::text AS "c5",
          COUNT(*) FILTER (WHERE COALESCE(m.n, 0) = 0)::text AS "c6",
          CASE WHEN COUNT(*) = 0 THEN '0.0'
            ELSE to_char(100.0 * COUNT(*) FILTER (WHERE COALESCE(m.n, 0) > 0) / COUNT(*), 'FM990.0') END AS "c7",
          COALESCE(to_char(MAX(m.last_at) + make_interval(mins => @OffsetMinutes), 'YYYY-MM-DD HH24:MI'), '') AS "c8"
        FROM scheduled s
        LEFT JOIN LATERAL (
          SELECT COUNT(*) AS n, MAX(p."UpdatedAt") AS last_at
          FROM "dbo"."PeriodAttendanceRecords" p
          WHERE p."Date" = s.day AND p."Period" = s.period
            AND (s.class_id IS NULL OR p."ClassId" = s.class_id)
            AND lower(p."Subject") = lower(s.subject)
        ) m ON true
        GROUP BY s.teacher, s.class_name, s.section, s.subject
        ORDER BY s.teacher, s.class_name, s.subject
        LIMIT 5000
        """;

    private static readonly string AttendanceCorrectionSql = """
        SELECT
          to_char(a."Date", 'YYYY-MM-DD') AS "c0",
          s."AdmissionNo" AS "c1",
          s."Name" AS "c2",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c3",
          COALESCE(s."Section", '') AS "c4",
          COALESCE(a."Subject", '') AS "c5",
          a."Period"::text AS "c6",
          COALESCE(a."FromStatus", '') AS "c7",
          COALESCE(a."ToStatus", '') AS "c8",
          COALESCE(a."ActorName", '') AS "c9",
          COALESCE(to_char(a."At" + make_interval(mins => @OffsetMinutes), 'YYYY-MM-DD HH24:MI'), '') AS "c10"
        FROM "dbo"."PeriodAttendanceAudit" a
        JOIN "dbo"."Students" s ON s."Id" = a."StudentId"
        WHERE a."Date" >= @FromDate::date AND a."Date" <= @ToDate::date
          AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
          AND (@Subject IS NULL OR a."Subject" ILIKE @Subject ESCAPE '\')
          AND (@Period IS NULL OR a."Period"::text = @Period)
          AND (@Teacher IS NULL OR a."ActorName" ILIKE @Teacher ESCAPE '\')
        ORDER BY a."At" DESC
        LIMIT 5000
        """;

    private static readonly string AttendanceTrendSql = "WITH " + StudentDayCtes + """
        SELECT
          to_char(date_trunc('week', ds.day)::date, 'YYYY-MM-DD') AS "c0",
          COUNT(*) FILTER (WHERE ds.st = 'present')::text AS "c1",
          COUNT(*) FILTER (WHERE ds.st = 'absent')::text AS "c2",
          COUNT(*) FILTER (WHERE ds.st = 'leave')::text AS "c3",
          COUNT(*) FILTER (WHERE ds.st = 'late')::text AS "c4",
          CASE WHEN COUNT(*) FILTER (WHERE ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(
              100.0 * COUNT(*) FILTER (WHERE ds.st IN ('present', 'late'))
              / COUNT(*) FILTER (WHERE ds.st IN ('present', 'absent', 'leave', 'late')),
              'FM990.0') END AS "c5"
        FROM day_status ds
        JOIN roster r ON r.id = ds.id
        GROUP BY date_trunc('week', ds.day)
        ORDER BY date_trunc('week', ds.day)
        """;

    private static readonly string AttendanceCompareSql = "WITH " + StudentDayCtes + """
        , bounds AS (
          SELECT @FromDate::date + ((@ToDate::date - @FromDate::date) / 2) AS mid
        )
        SELECT
          r.class_name AS "c0",
          r.section AS "c1",
          CASE WHEN COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(100.0 * COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'late'))
              / COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 'FM990.0') END AS "c2",
          CASE WHEN COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(100.0 * COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'late'))
              / COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 'FM990.0') END AS "c3",
          to_char(
            COALESCE(100.0 * COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'late'))
              / NULLIF(COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 0), 0)
            - COALESCE(100.0 * COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'late'))
              / NULLIF(COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 0), 0)
          , 'FM990.0') AS "c4"
        FROM day_status ds
        JOIN roster r ON r.id = ds.id
        CROSS JOIN bounds b
        GROUP BY r.class_name, r.section
        ORDER BY r.class_name, r.section
        LIMIT 5000
        """;

    private static readonly string AttendanceShiftSql = "WITH " + StudentDayCtes + """
        , bounds AS (
          SELECT @FromDate::date + ((@ToDate::date - @FromDate::date) / 2) AS mid
        )
        SELECT
          r.adm AS "c0",
          r.name AS "c1",
          r.class_name AS "c2",
          r.section AS "c3",
          CASE WHEN COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(100.0 * COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'late'))
              / COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 'FM990.0') END AS "c4",
          CASE WHEN COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')) = 0 THEN '0.0'
            ELSE to_char(100.0 * COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'late'))
              / COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 'FM990.0') END AS "c5",
          to_char(
            COALESCE(100.0 * COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'late'))
              / NULLIF(COUNT(*) FILTER (WHERE ds.day > b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 0), 0)
            - COALESCE(100.0 * COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'late'))
              / NULLIF(COUNT(*) FILTER (WHERE ds.day <= b.mid AND ds.st IN ('present', 'absent', 'leave', 'late')), 0), 0)
          , 'FM990.0') AS "c6"
        FROM day_status ds
        JOIN roster r ON r.id = ds.id
        CROSS JOIN bounds b
        GROUP BY r.id, r.adm, r.name, r.class_name, r.section
        ORDER BY r.class_name, r.name
        LIMIT 5000
        """;

    private static readonly string AbsenceAnalysisSql = "WITH " + StudentDayCtes + """
        SELECT
          trim(to_char(ds.day, 'Day')) AS "c0",
          COUNT(*)::text AS "c1",
          COUNT(DISTINCT ds.id)::text AS "c2"
        FROM day_status ds
        WHERE ds.st = 'absent'
        GROUP BY trim(to_char(ds.day, 'Day')), EXTRACT(DOW FROM ds.day)
        ORDER BY EXTRACT(DOW FROM ds.day)
        """;

    private static readonly string StaffAttendanceSql = """
        WITH people AS (
          SELECT t."Id" AS person_id, 'teacher'::text AS kind, t."UserId" AS user_id,
                 COALESCE(t."EmployeeCode", '') AS code, t."Name" AS name,
                 COALESCE(t."Department", '') AS dept, COALESCE(t."Designation", '') AS designation
          FROM "dbo"."Teachers" t
          UNION ALL
          SELECT st."Id", 'staff', st."UserId", COALESCE(st."EmployeeCode", ''), st."Name",
                 COALESCE(st."Department", ''), COALESCE(st."Role", '')
          FROM "dbo"."Staff" st
        ),
        activity AS (
          SELECT p.person_id, p.kind, a."Date" AS day, a."Status" AS mark
          FROM people p
          JOIN "dbo"."StaffAttendanceRecords" a
            ON a."PersonId" = p.person_id AND a."PersonType" = p.kind
           AND a."Date" BETWEEN @FromDate::date AND @ToDate::date
          UNION
          SELECT p.person_id, p.kind, (c."At" + make_interval(mins => @OffsetMinutes))::date, NULL::varchar
          FROM people p
          JOIN "dbo"."CheckIns" c ON c."UserId" = p.user_id
          WHERE p.user_id IS NOT NULL
            AND (c."At" + make_interval(mins => @OffsetMinutes))::date BETWEEN @FromDate::date AND @ToDate::date
        ),
        spine AS (
          SELECT person_id, kind, day, MAX(mark) AS mark FROM activity GROUP BY person_id, kind, day
        )
        SELECT
          p.code AS "c0",
          p.name AS "c1",
          p.dept AS "c2",
          p.designation AS "c3",
          to_char(s.day, 'YYYY-MM-DD') AS "c4",
          COALESCE(to_char(ci.in_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c5",
          COALESCE(to_char(ci.out_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c6",
          CASE WHEN ci.in_at IS NOT NULL AND ci.out_at IS NOT NULL AND ci.out_at > ci.in_at
            THEN to_char(EXTRACT(EPOCH FROM (ci.out_at - ci.in_at)) / 3600, 'FM990.00') ELSE '' END AS "c7",
          COALESCE(slots.periods, '') AS "c8",
          COALESCE(slots.subjects, '') AS "c9",
          COALESCE(lv.kind, CASE WHEN s.mark ILIKE '%leave%' THEN s.mark ELSE '' END, '') AS "c10",
          CASE LOWER(COALESCE(s.mark, ''))
            WHEN 'present' THEN 'P' WHEN 'absent' THEN 'A' WHEN 'late' THEN 'L'
            WHEN 'half_day' THEN 'H' WHEN 'pending' THEN 'N' WHEN 'leave' THEN 'LV'
            WHEN '' THEN '' ELSE s.mark END AS "c11"
        FROM spine s
        JOIN people p ON p.person_id = s.person_id AND p.kind = s.kind
        LEFT JOIN LATERAL (
          SELECT MIN(c."At") FILTER (WHERE c."Kind" = 'in') AS in_at,
                 MAX(c."At") FILTER (WHERE c."Kind" = 'out') AS out_at
          FROM "dbo"."CheckIns" c
          WHERE p.user_id IS NOT NULL AND c."UserId" = p.user_id
            AND (c."At" + make_interval(mins => @OffsetMinutes))::date = s.day
        ) ci ON true
        LEFT JOIN LATERAL (
          SELECT string_agg(x.period, ', ' ORDER BY x.period::int) AS periods,
                 string_agg(x.subject, ', ' ORDER BY x.period::int) AS subjects
          FROM (
            SELECT DISTINCT ts."Period"::text AS period, NULLIF(ts."Subject", '') AS subject
            FROM "dbo"."TimetableSlots" ts
            WHERE p.kind = 'teacher' AND ts."TeacherId" = p.person_id
              AND upper(left(COALESCE(ts."Day", ''), 3)) =
                  upper((ARRAY['Sun','Mon','Tue','Wed','Thu','Fri','Sat'])[EXTRACT(DOW FROM s.day)::int + 1])
          ) x
        ) slots ON true
        LEFT JOIN LATERAL (
          SELECT lr."Type" AS kind
          FROM "dbo"."LeaveRequests" lr
          WHERE p.user_id IS NOT NULL AND lr."RequesterId" = p.user_id
            AND lr."Status" ILIKE 'approv%'
            AND s.day BETWEEN lr."FromDate" AND COALESCE(lr."ToDate", lr."FromDate")
          ORDER BY lr."FromDate" DESC
          LIMIT 1
        ) lv ON true
        WHERE (@FromTime IS NULL OR ci.in_at IS NULL OR (ci.in_at + make_interval(mins => @OffsetMinutes))::time >= @FromTime::time)
          AND (@ToTime IS NULL OR ci.in_at IS NULL OR (ci.in_at + make_interval(mins => @OffsetMinutes))::time <= @ToTime::time)
          AND (@Employee IS NULL OR p.name ILIKE @Employee ESCAPE '\' OR p.code ILIKE @Employee ESCAPE '\')
          AND (@Department IS NULL OR LOWER(p.dept) = LOWER(@Department))
          AND (@Designation IS NULL OR LOWER(p.designation) = LOWER(@Designation))
          AND (@AttendanceStatus IS NULL OR LOWER(COALESCE(s.mark, '')) = LOWER(@AttendanceStatus))
        ORDER BY s.day DESC, p.name
        LIMIT 5000
        """;

    private static readonly string ParentSql = """
        SELECT
          LEFT(u."Id"::text, 8) AS "c0",
          COALESCE(NULLIF(u."Name", ''), s."GuardianName", '') AS "c1",
          COALESCE(l."Relationship", s."GuardianRelation", '') AS "c2",
          COALESCE(NULLIF(u."Phone", ''), s."GuardianPhone", '') AS "c3",
          COALESCE(NULLIF(u."Email", ''), s."GuardianEmail", '') AS "c4",
          s."Name" AS "c5",
          s."AdmissionNo" AS "c6",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c7",
          COALESCE(s."Section", '') AS "c8",
          COALESCE(s."Address", '') AS "c9",
          CASE WHEN EXISTS (SELECT 1 FROM "dbo"."PushDevices" d WHERE d."UserId" = u."Id") THEN 'Registered' ELSE 'Not registered' END AS "c10",
          COALESCE(to_char(u."LastSeenAt" + make_interval(mins => @OffsetMinutes), 'YYYY-MM-DD HH24:MI'), '') AS "c11",
          COALESCE(u."Status", '') AS "c12"
        FROM "dbo"."ParentStudentLinks" l
        JOIN "dbo"."Users" u ON u."Id" = l."ParentUserId"
        JOIN "dbo"."Students" s ON s."Id" = l."StudentId"
        WHERE (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\' OR COALESCE(u."Name", '') ILIKE @Student ESCAPE '\')
        ORDER BY s."Name"
        LIMIT 5000
        """;

    private static readonly string TransportSql = """
        SELECT
          s."Name" AS "c0",
          s."AdmissionNo" AS "c1",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c2",
          COALESCE(s."Section", '') AS "c3",
          COALESCE(b."RouteName", '') AS "c4",
          COALESCE(b."BusNo", '') AS "c5",
          COALESCE(b."Driver", '') AS "c6",
          COALESCE(b."DriverPhone", '') AS "c7",
          COALESCE(st."Name", '') AS "c8",
          '' AS "c9",
          COALESCE(st."Time", '') AS "c10",
          '' AS "c11",
          to_char(COALESCE(fee.amt, 0), 'FM999999990.00') AS "c12",
          CASE WHEN EXISTS (SELECT 1 FROM "dbo"."StudentTransportOptOut" o WHERE o."StudentId" = s."Id")
            THEN 'Opted out' ELSE 'Assigned' END AS "c13"
        FROM "dbo"."StudentBusAssignments" sa
        JOIN "dbo"."Students" s ON s."Id" = sa."StudentId"
        LEFT JOIN "dbo"."Buses" b ON b."Id" = sa."BusId"
        LEFT JOIN "dbo"."BusStops" st ON st."Id" = sa."StopId"
        LEFT JOIN LATERAL (
          SELECT SUM(l."Amount") AS amt
          FROM "dbo"."FeeInvoiceLines" l
          JOIN "dbo"."FeeHeads" h ON h."Id" = l."FeeHeadId" AND h."IsTransportFeeHead" = true
          JOIN "dbo"."FeeInvoices" i ON i."Id" = l."InvoiceId" AND i."StudentId" = s."Id"
        ) fee ON true
        WHERE (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
          AND (@Route IS NULL OR LOWER(COALESCE(b."RouteName", '')) = LOWER(@Route))
          AND (@Vehicle IS NULL OR LOWER(COALESCE(b."BusNo", '')) = LOWER(@Vehicle))
        ORDER BY b."RouteName", s."Name"
        LIMIT 5000
        """;

    private static readonly string AcademicSql = """
        SELECT
          s."AdmissionNo" AS "c0",
          s."Name" AS "c1",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c2",
          COALESCE(s."Section", '') AS "c3",
          COALESCE(NULLIF(p."Subject", ''), p."Name", '') AS "c4",
          COALESCE(e."Name", '') AS "c5",
          to_char(g."MaxMarks", 'FM999999990.00') AS "c6",
          to_char(g."Marks", 'FM999999990.00') AS "c7",
          CASE WHEN g."MaxMarks" > 0 THEN to_char(ROUND(g."Marks" / g."MaxMarks" * 100, 1), 'FM990.0') ELSE '' END AS "c8",
          COALESCE(g."Grade", '') AS "c9",
          CASE WHEN g."Pass" THEN 'Pass' ELSE 'Fail' END AS "c10",
          COALESCE(t."Name", '') AS "c11"
        FROM "dbo"."Grades" g
        JOIN "dbo"."Students" s ON s."Id" = g."StudentId"
        JOIN "dbo"."ExamPapers" p ON p."Id" = g."ExamPaperId"
        LEFT JOIN "dbo"."Exams" e ON e."Id" = p."ExamId"
        LEFT JOIN LATERAL (
          SELECT te."Name" FROM "dbo"."Subjects" sub
          JOIN "dbo"."Teachers" te ON te."Id" = sub."TeacherId"
          WHERE LOWER(sub."Name") = LOWER(COALESCE(p."Subject", ''))
          LIMIT 1
        ) t ON true
        WHERE (p."Date" IS NULL OR (p."Date" >= @FromDate::date AND p."Date" <= @ToDate::date))
          AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
          AND (@Teacher IS NULL OR COALESCE(t."Name", '') ILIKE @Teacher ESCAPE '\')
        ORDER BY s."Name", p."Subject"
        LIMIT 5000
        """;

    private const string OptionsSql = """
        SELECT DISTINCT "Value" FROM (
          SELECT COALESCE(NULLIF("ClassLabel", ''), NULLIF("Grade", '')) AS "Value" FROM "dbo"."Students"
          UNION
          SELECT NULLIF(btrim("Name"), '') FROM "dbo"."Classes"
        ) c WHERE COALESCE("Value", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Value" FROM (
          SELECT "Section" AS "Value" FROM "dbo"."Students"
          UNION
          SELECT "Section" FROM "dbo"."Classes"
        ) s WHERE COALESCE("Value", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Department" AS "Value" FROM (
          SELECT "Department" FROM "dbo"."Teachers" UNION SELECT "Department" FROM "dbo"."Staff"
        ) d WHERE COALESCE("Department", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Designation" AS "Value" FROM (
          SELECT "Designation" AS "Designation" FROM "dbo"."Teachers" UNION SELECT "Role" FROM "dbo"."Staff"
        ) d WHERE COALESCE("Designation", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Role" AS "Value" FROM (
          SELECT "Role" FROM "dbo"."UserRoles" UNION SELECT "Role" FROM "dbo"."Staff" UNION SELECT 'Teacher'
        ) r WHERE COALESCE("Role", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Name" AS "Value" FROM "dbo"."FeeHeads" WHERE COALESCE("Name", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Method" AS "Value" FROM "dbo"."FeePayments" WHERE COALESCE("Method", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Status" AS "Value" FROM "dbo"."FeeInvoices" WHERE COALESCE("Status", '') <> '' ORDER BY 1;
        SELECT DISTINCT "Status" AS "Value" FROM (
          SELECT "Status" FROM "dbo"."AttendanceRecords" UNION SELECT "Status" FROM "dbo"."StaffAttendanceRecords" UNION SELECT 'pending'
        ) a WHERE COALESCE("Status", '') <> '' ORDER BY 1;
        SELECT DISTINCT "RouteName" AS "Value" FROM "dbo"."Buses" WHERE COALESCE("RouteName", '') <> '' ORDER BY 1;
        SELECT DISTINCT "BusNo" AS "Value" FROM "dbo"."Buses" WHERE COALESCE("BusNo", '') <> '' ORDER BY 1;
        """;
}
