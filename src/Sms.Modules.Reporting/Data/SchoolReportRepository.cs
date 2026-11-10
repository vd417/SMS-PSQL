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
            "staff" => 15,
            "teacher" => 13,
            "admin" => 12,
            "fee_collection" => 16,
            "fee_defaulters" => 15,
            "student_attendance" => 11,
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
        SELECT * FROM (
          SELECT
            COALESCE(t."EmployeeCode", '') AS "c0",
            t."Name" AS "c1",
            COALESCE(t."Designation", '') AS "c2",
            'Teacher' AS "c3",
            COALESCE(t."Department", '') AS "c4",
            CASE UPPER(COALESCE(t."Gender", '')) WHEN 'M' THEN 'Male' WHEN 'F' THEN 'Female' ELSE '' END AS "c5",
            COALESCE(t."Phone", '') AS "c6",
            COALESCE(t."Email", '') AS "c7",
            to_char((t."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD') AS "c8",
            '' AS "c9",
            '' AS "c10",
            COALESCE(t."ClassTeacher", '') AS "c11",
            '' AS "c12",
            to_char(t."AttendancePct", 'FM990.00') AS "c13",
            COALESCE(t."Status", '') AS "c14",
            t."Gender" AS g
          FROM "dbo"."Teachers" t
          UNION ALL
          SELECT
            COALESCE(st."EmployeeCode", ''),
            st."Name",
            COALESCE(st."Role", ''),
            COALESCE(NULLIF(st."Role", ''), 'Staff'),
            COALESCE(st."Department", ''),
            CASE UPPER(COALESCE(st."Gender", '')) WHEN 'M' THEN 'Male' WHEN 'F' THEN 'Female' ELSE '' END,
            COALESCE(st."Phone", ''),
            COALESCE(st."Email", ''),
            to_char((st."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD'),
            '',
            '',
            '',
            '',
            to_char(st."AttendancePct", 'FM990.00'),
            COALESCE(st."Status", ''),
            st."Gender"
          FROM "dbo"."Staff" st
        ) q
        WHERE (@Employee IS NULL OR q."c0" ILIKE @Employee ESCAPE '\' OR q."c1" ILIKE @Employee ESCAPE '\')
          AND (@Department IS NULL OR LOWER(q."c4") = LOWER(@Department))
          AND (@Designation IS NULL OR LOWER(q."c2") = LOWER(@Designation))
          AND (@Role IS NULL OR LOWER(q."c3") = LOWER(@Role))
          AND (@Gender IS NULL OR ((@Gender = 'O' AND UPPER(COALESCE(q.g, '')) NOT IN ('M', 'F')) OR UPPER(COALESCE(q.g, '')) = @Gender))
        ORDER BY q."c1"
        LIMIT 5000
        """;

    private static readonly string TeacherSql = """
        SELECT
          LEFT(t."Id"::text, 8) AS "c0",
          COALESCE(t."EmployeeCode", '') AS "c1",
          t."Name" AS "c2",
          COALESCE(t."Department", '') AS "c3",
          COALESCE(t."SubjectsCsv", '') AS "c4",
          COALESCE(cls.names, '') AS "c5",
          COALESCE(cls.sections, '') AS "c6",
          COALESCE(t."ClassTeacher", '') AS "c7",
          COALESCE(stu.n, 0)::text AS "c8",
          t."Load"::text AS "c9",
          to_char((t."CreatedAt" + make_interval(mins => @OffsetMinutes))::date, 'YYYY-MM-DD') AS "c10",
          to_char(t."AttendancePct", 'FM990.00') AS "c11",
          COALESCE(t."Status", '') AS "c12"
        FROM "dbo"."Teachers" t
        LEFT JOIN LATERAL (
          SELECT string_agg(DISTINCT COALESCE(NULLIF(c."Name", ''), c."Grade"), ', ') AS names,
                 string_agg(DISTINCT NULLIF(c."Section", ''), ', ') AS sections
          FROM "dbo"."Classes" c WHERE c."ClassTeacherId" = t."Id"
        ) cls ON true
        LEFT JOIN LATERAL (
          SELECT COUNT(*)::int AS n FROM "dbo"."Students" s
          WHERE COALESCE(t."ClassTeacher", '') <> ''
            AND LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(t."ClassTeacher")
        ) stu ON true
        WHERE (@Teacher IS NULL OR t."Name" ILIKE @Teacher ESCAPE '\' OR COALESCE(t."EmployeeCode", '') ILIKE @Teacher ESCAPE '\')
          AND (@Department IS NULL OR LOWER(COALESCE(t."Department", '')) = LOWER(@Department))
        """ + G("t") + """
        ORDER BY t."Name"
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
        SELECT
          to_char(a."Date", 'YYYY-MM-DD') AS "c0",
          s."AdmissionNo" AS "c1",
          s."Name" AS "c2",
          COALESCE(NULLIF(s."ClassLabel", ''), NULLIF(s."Grade", ''), '') AS "c3",
          COALESCE(s."Section", '') AS "c4",
          CASE WHEN s."Roll" = 0 THEN '' ELSE s."Roll"::text END AS "c5",
          COALESCE(a."Status", '') AS "c6",
          '' AS "c7",
          '' AS "c8",
          COALESCE(u."Name", '') AS "c9",
          '' AS "c10"
        FROM "dbo"."AttendanceRecords" a
        JOIN "dbo"."Students" s ON s."Id" = a."StudentId"
        LEFT JOIN "dbo"."Users" u ON u."Id" = a."MarkedBy"
        WHERE a."Date" >= @FromDate::date AND a."Date" <= @ToDate::date
          AND (@ClassName IS NULL OR LOWER(COALESCE(NULLIF(s."ClassLabel", ''), s."Grade", '')) = LOWER(@ClassName))
          AND (@Section IS NULL OR LOWER(COALESCE(s."Section", '')) = LOWER(@Section))
          AND (@Student IS NULL OR s."Name" ILIKE @Student ESCAPE '\' OR s."AdmissionNo" ILIKE @Student ESCAPE '\')
          AND (@AttendanceStatus IS NULL OR LOWER(a."Status") = LOWER(@AttendanceStatus))
        ORDER BY a."Date" DESC, s."Name"
        LIMIT 5000
        """;

    private static readonly string StaffAttendanceSql = """
        SELECT
          COALESCE(t."EmployeeCode", st."EmployeeCode", '') AS "c0",
          COALESCE(t."Name", st."Name", '') AS "c1",
          COALESCE(t."Department", st."Department", '') AS "c2",
          COALESCE(t."Designation", st."Role", '') AS "c3",
          to_char(a."Date", 'YYYY-MM-DD') AS "c4",
          COALESCE(to_char(ci.in_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c5",
          COALESCE(to_char(ci.out_at + make_interval(mins => @OffsetMinutes), 'HH24:MI'), '') AS "c6",
          CASE WHEN ci.in_at IS NOT NULL AND ci.out_at IS NOT NULL AND ci.out_at > ci.in_at
            THEN to_char(EXTRACT(EPOCH FROM (ci.out_at - ci.in_at)) / 3600, 'FM990.00') ELSE '' END AS "c7",
          '' AS "c8",
          CASE WHEN a."Status" ILIKE '%leave%' THEN a."Status" ELSE '' END AS "c9",
          COALESCE(a."Status", '') AS "c10",
          '' AS "c11"
        FROM "dbo"."StaffAttendanceRecords" a
        LEFT JOIN "dbo"."Teachers" t ON a."PersonType" = 'teacher' AND t."Id" = a."PersonId"
        LEFT JOIN "dbo"."Staff" st ON a."PersonType" = 'staff' AND st."Id" = a."PersonId"
        LEFT JOIN LATERAL (
          SELECT MIN("At") FILTER (WHERE "Kind" = 'in') AS in_at,
                 MAX("At") FILTER (WHERE "Kind" = 'out') AS out_at
          FROM "dbo"."CheckIns" c
          WHERE c."UserId" = COALESCE(t."UserId", st."UserId")
            AND (c."At" + make_interval(mins => @OffsetMinutes))::date = a."Date"
        ) ci ON true
        WHERE a."Date" >= @FromDate::date AND a."Date" <= @ToDate::date
          AND (@FromTime IS NULL OR ci.in_at IS NULL OR (ci.in_at + make_interval(mins => @OffsetMinutes))::time >= @FromTime::time)
          AND (@ToTime IS NULL OR ci.in_at IS NULL OR (ci.in_at + make_interval(mins => @OffsetMinutes))::time <= @ToTime::time)
          AND (@Employee IS NULL OR COALESCE(t."Name", st."Name", '') ILIKE @Employee ESCAPE '\' OR COALESCE(t."EmployeeCode", st."EmployeeCode", '') ILIKE @Employee ESCAPE '\')
          AND (@Department IS NULL OR LOWER(COALESCE(t."Department", st."Department", '')) = LOWER(@Department))
          AND (@Designation IS NULL OR LOWER(COALESCE(t."Designation", st."Role", '')) = LOWER(@Designation))
          AND (@AttendanceStatus IS NULL OR LOWER(a."Status") = LOWER(@AttendanceStatus))
        ORDER BY a."Date" DESC, 2
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
          SELECT "Status" FROM "dbo"."AttendanceRecords" UNION SELECT "Status" FROM "dbo"."StaffAttendanceRecords"
        ) a WHERE COALESCE("Status", '') <> '' ORDER BY 1;
        SELECT DISTINCT "RouteName" AS "Value" FROM "dbo"."Buses" WHERE COALESCE("RouteName", '') <> '' ORDER BY 1;
        SELECT DISTINCT "BusNo" AS "Value" FROM "dbo"."Buses" WHERE COALESCE("BusNo", '') <> '' ORDER BY 1;
        """;
}
