using Dapper;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class PeriodAttendanceQueryRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<PeriodAttendanceAdvancedPage> SearchAsync(
        PeriodAttendanceAdvancedQuery q,
        CancellationToken ct = default)
    {
        var command = PeriodAttendanceQuerySql.Build(q);
        await using var conn = await Factory.OpenAsync(ct);
        using var results = await conn.QueryMultipleAsync(
            new CommandDefinition(command.Sql, command.Parameters, cancellationToken: ct));
        var totalCount = await results.ReadSingleAsync<int>();
        var items = (await results.ReadAsync<PeriodAttendanceAdvancedRow>()).AsList();

        return command.ToPage(items, totalCount);
    }

    public async Task<AdvClassDaySummary> SummarizeClassDayAsync(
        Guid classId,
        DateOnly date,
        CancellationToken ct = default)
    {
        var command = PeriodAttendanceAggregateSql.BuildClassDay(classId, date);
        await using var conn = await Factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<PeriodAttendanceClassDayRow>(
            new CommandDefinition(command.Sql, command.Parameters, cancellationToken: ct));
        return row.ToContract();
    }

    public async Task<IReadOnlyList<AdvSubjectSummaryRow>> SummarizeSubjectsAsync(
        Guid classId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var command = PeriodAttendanceAggregateSql.BuildSubjects(classId, from, to);
        await using var conn = await Factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PeriodAttendanceSubjectRow>(
            new CommandDefinition(command.Sql, command.Parameters, cancellationToken: ct));
        return rows.Select(row => row.ToContract()).ToList();
    }

    public async Task<IReadOnlyList<AdvTeacherSummaryRow>> SummarizeTeachersAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default)
    {
        var command = PeriodAttendanceAggregateSql.BuildTeachers(from, to);
        await using var conn = await Factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PeriodAttendanceTeacherRow>(
            new CommandDefinition(command.Sql, command.Parameters, cancellationToken: ct));
        return rows.Select(row => row.ToContract()).ToList();
    }

    public async Task<AdvRangeRollup> SummarizeRangeAsync(
        DateOnly from,
        DateOnly to,
        Guid? classId = null,
        string? grade = null,
        string? section = null,
        Guid? studentId = null,
        string? subject = null,
        Guid? teacherId = null,
        CancellationToken ct = default)
    {
        var command = PeriodAttendanceAggregateSql.BuildRange(
            from, to, classId, grade, section, studentId, subject, teacherId);
        await using var conn = await Factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<PeriodAttendanceRangeRow>(
            new CommandDefinition(command.Sql, command.Parameters, cancellationToken: ct));
        return row.ToContract();
    }

    public async Task<IReadOnlyList<PeriodAttendanceAuditRow>> GetAuditAsync(
        Guid recordId,
        CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PeriodAttendanceAuditRow>(
            new CommandDefinition(
                """
                SELECT "Id", "RecordId", "ClassId", "StudentId", "Date", "Period", "Subject",
                       "FromStatus", "ToStatus", "ActorId", "ActorName", "ActorRole", "At"
                FROM "dbo"."PeriodAttendanceAudit"
                WHERE "RecordId" = @RecordId
                ORDER BY "At" DESC
                """,
                new { RecordId = recordId },
                cancellationToken: ct));
        return rows.AsList();
    }
}

public sealed record PeriodAttendanceClassDayRow(
    int TotalStudents,
    int Present,
    int Absent,
    int Late,
    int Leave,
    int TotalPeriods,
    int MarkedPeriods)
{
    public AdvClassDaySummary ToContract()
    {
        var counts = PeriodAttendanceMath.FromStatusBuckets(Present, Late, Absent, Leave);
        return new AdvClassDaySummary(
            TotalStudents,
            Present,
            Absent,
            Late,
            Leave,
            Math.Max(0, (TotalStudents * TotalPeriods) - counts.TotalMarkedPeriods),
            counts.AttendancePercentage,
            TotalPeriods,
            MarkedPeriods,
            Math.Max(0, TotalPeriods - MarkedPeriods));
    }
}

public sealed record PeriodAttendanceSubjectRow(
    string Subject,
    string? TeacherName,
    int Periods,
    int Marked,
    int Present,
    int Absent,
    int Late,
    int Leave)
{
    public AdvSubjectSummaryRow ToContract()
    {
        var counts = PeriodAttendanceMath.FromStatusBuckets(Present, Late, Absent, Leave);
        return new AdvSubjectSummaryRow(
            Subject,
            TeacherName,
            Periods,
            Marked,
            Math.Max(0, Periods - Marked),
            Present,
            Absent,
            Late,
            counts.AttendancePercentage);
    }
}

public sealed record PeriodAttendanceTeacherRow(
    Guid TeacherId,
    string TeacherName,
    int Classes,
    int Sections,
    int Subjects,
    int ExpectedPeriods,
    int MarkedPeriods,
    int TeacherMarked,
    int StaffMarked,
    int PrincipalMarked,
    int AdminMarked)
{
    public AdvTeacherSummaryRow ToContract() =>
        new(
            TeacherId,
            TeacherName,
            Classes,
            Sections,
            Subjects,
            ExpectedPeriods,
            MarkedPeriods,
            Math.Max(0, ExpectedPeriods - MarkedPeriods),
            TeacherMarked,
            StaffMarked,
            PrincipalMarked,
            AdminMarked);
}

public sealed record PeriodAttendanceRangeRow(int Present, int Absent, int Late, int Leave)
{
    public AdvRangeRollup ToContract()
    {
        var counts = PeriodAttendanceMath.FromStatusBuckets(Present, Late, Absent, Leave);
        return new AdvRangeRollup(
            counts.TotalMarkedPeriods,
            Present,
            Absent,
            Late,
            Leave,
            counts.AttendancePercentage);
    }
}

public sealed record PeriodAttendanceAggregateCommand(string Sql, DynamicParameters Parameters);

public static class PeriodAttendanceAggregateSql
{
    // SQL Server's recursive CTE (implicit recursion via self-reference) requires the explicit
    // RECURSIVE keyword in Postgres; DATEADD(DAY, 1, x) -> date + integer; DATENAME(WEEKDAY, x)
    // -> to_char(x, 'DY') (locale-dependent 3-letter abbreviation, same as the source's own
    // LEFT(...,3) truncation -- both sides of every day-name comparison go through the same
    // upper(to_char(...)) so a locale mismatch would show up as a symmetric non-match, not a
    // silent wrong answer). OPTION (MAXRECURSION 32767) has no Postgres equivalent and is
    // dropped -- Postgres recursive CTEs aren't statement-level depth-limited the same way.
    private const string DateSeries = """
        WITH RECURSIVE "Dates" AS (
            SELECT @From::date AS "Date"
            UNION ALL
            SELECT "Date" + 1
            FROM "Dates"
            WHERE "Date" < @To::date
        )
        """;

    public static PeriodAttendanceAggregateCommand BuildClassDay(Guid classId, DateOnly date)
    {
        const string sql = """
            WITH "ExpectedSessions" AS (
                SELECT ts."Period", lower(trim(ts."Subject")) AS "Subject"
                FROM "dbo"."TimetableSlots" ts
                WHERE ts."ClassId" = @ClassId
                  AND upper(left(trim(ts."Day"), 3))
                      = upper(left(to_char(@Date::date, 'DY'), 3))
            ),
            "StatusCounts" AS (
                SELECT
                    COALESCE(SUM(CASE WHEN par."Status" = 'present' THEN 1 ELSE 0 END), 0) AS "Present",
                    COALESCE(SUM(CASE WHEN par."Status" = 'absent' THEN 1 ELSE 0 END), 0) AS "Absent",
                    COALESCE(SUM(CASE WHEN par."Status" = 'late' THEN 1 ELSE 0 END), 0) AS "Late",
                    COALESCE(SUM(CASE WHEN par."Status" = 'leave' THEN 1 ELSE 0 END), 0) AS "Leave"
                FROM "ExpectedSessions" es
                LEFT JOIN "dbo"."PeriodAttendanceRecords" par
                  ON par."ClassId" = @ClassId
                 AND par."Date" = @Date
                 AND par."Period" = es."Period"
                 AND lower(trim(par."Subject")) = es."Subject"
            ),
            "MarkedSessions" AS (
                SELECT par."Period", lower(trim(par."Subject")) AS "Subject"
                FROM "dbo"."PeriodAttendanceRecords" par
                WHERE par."ClassId" = @ClassId AND par."Date" = @Date
                GROUP BY par."Period", lower(trim(par."Subject"))
            )
            SELECT
                CAST((SELECT COUNT(*)
                 FROM "dbo"."Classes" c
                 INNER JOIN "dbo"."Students" s
                   ON (c."Grade" IS NOT NULL AND c."Section" IS NOT NULL
                       AND s."Grade" = c."Grade" AND s."Section" = c."Section")
                   OR (c."Name" IS NOT NULL AND s."ClassLabel" = c."Name")
                 WHERE c."Id" = @ClassId AND s."Status" = 'active') AS int) AS "TotalStudents",
                CAST(sc."Present" AS int) AS "Present",
                CAST(sc."Absent" AS int) AS "Absent",
                CAST(sc."Late" AS int) AS "Late",
                CAST(sc."Leave" AS int) AS "Leave",
                CAST((SELECT COUNT(*) FROM "ExpectedSessions") AS int) AS "TotalPeriods",
                CAST((SELECT COUNT(*) FROM "MarkedSessions" ms
                 WHERE EXISTS (
                     SELECT 1 FROM "ExpectedSessions" es
                     WHERE es."Period" = ms."Period" AND es."Subject" = ms."Subject"
                 )) AS int) AS "MarkedPeriods"
            FROM "StatusCounts" sc;
            """;

        var parameters = new DynamicParameters();
        parameters.Add("ClassId", classId);
        parameters.Add("Date", date.ToDateTime(TimeOnly.MinValue));
        return new PeriodAttendanceAggregateCommand(sql, parameters);
    }

    public static PeriodAttendanceAggregateCommand BuildSubjects(
        Guid classId,
        DateOnly from,
        DateOnly to)
    {
        ValidateRange(from, to);
        var sql = DateSeries + """
            , "ExpectedSessions" AS (
                SELECT
                    d."Date",
                    ts."ClassId",
                    ts."Period",
                    trim(ts."Subject") AS "Subject",
                    lower(trim(ts."Subject")) AS "SubjectKey",
                    t."Name" AS "TeacherName"
                FROM "Dates" d
                INNER JOIN "dbo"."TimetableSlots" ts
                  ON ts."ClassId" = @ClassId
                 AND upper(left(trim(ts."Day"), 3))
                     = upper(left(to_char(d."Date", 'DY'), 3))
                LEFT JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
                WHERE nullif(trim(ts."Subject"), '') IS NOT NULL
            ),
            "MarkedSessions" AS (
                SELECT
                    par."Date",
                    par."ClassId",
                    par."Period",
                    lower(trim(par."Subject")) AS "SubjectKey",
                    SUM(CASE WHEN par."Status" = 'present' THEN 1 ELSE 0 END) AS "Present",
                    SUM(CASE WHEN par."Status" = 'absent' THEN 1 ELSE 0 END) AS "Absent",
                    SUM(CASE WHEN par."Status" = 'late' THEN 1 ELSE 0 END) AS "Late",
                    SUM(CASE WHEN par."Status" = 'leave' THEN 1 ELSE 0 END) AS "Leave"
                FROM "dbo"."PeriodAttendanceRecords" par
                WHERE par."ClassId" = @ClassId
                  AND par."Date" >= @From AND par."Date" <= @To
                GROUP BY par."Date", par."ClassId", par."Period",
                         lower(trim(par."Subject"))
            )
            SELECT
                es."Subject",
                es."TeacherName",
                CAST(COUNT(*) AS int) AS "Periods",
                CAST(SUM(CASE WHEN ms."Period" IS NOT NULL THEN 1 ELSE 0 END) AS int) AS "Marked",
                CAST(COALESCE(SUM(ms."Present"), 0) AS int) AS "Present",
                CAST(COALESCE(SUM(ms."Absent"), 0) AS int) AS "Absent",
                CAST(COALESCE(SUM(ms."Late"), 0) AS int) AS "Late",
                CAST(COALESCE(SUM(ms."Leave"), 0) AS int) AS "Leave"
            FROM "ExpectedSessions" es
            LEFT JOIN "MarkedSessions" ms
              ON ms."Date" = es."Date"
             AND ms."ClassId" = es."ClassId"
             AND ms."Period" = es."Period"
             AND ms."SubjectKey" = es."SubjectKey"
            GROUP BY es."Subject", es."TeacherName"
            ORDER BY es."Subject", es."TeacherName";
            """;

        return BuildDateRangeCommand(sql, from, to, ("ClassId", classId));
    }

    public static PeriodAttendanceAggregateCommand BuildTeachers(DateOnly from, DateOnly to)
    {
        ValidateRange(from, to);
        var sql = DateSeries + """
            , "ExpectedSessions" AS (
                SELECT
                    d."Date",
                    ts."ClassId",
                    c."Grade",
                    c."Section",
                    ts."Period",
                    trim(ts."Subject") AS "Subject",
                    lower(trim(ts."Subject")) AS "SubjectKey",
                    ts."TeacherId",
                    t."Name" AS "TeacherName"
                FROM "Dates" d
                INNER JOIN "dbo"."TimetableSlots" ts
                  ON upper(left(trim(ts."Day"), 3))
                     = upper(left(to_char(d."Date", 'DY'), 3))
                INNER JOIN "dbo"."Classes" c ON c."Id" = ts."ClassId"
                INNER JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
                WHERE ts."TeacherId" IS NOT NULL
                  AND nullif(trim(ts."Subject"), '') IS NOT NULL
            ),
            "MarkedSessions" AS (
                SELECT
                    par."Date",
                    par."ClassId",
                    par."Period",
                    lower(trim(par."Subject")) AS "SubjectKey",
                    MAX(replace(lower(par."MarkedByRole"), 'school.', '')) AS "MarkerRole"
                FROM "dbo"."PeriodAttendanceRecords" par
                WHERE par."Date" >= @From AND par."Date" <= @To
                GROUP BY par."Date", par."ClassId", par."Period",
                         lower(trim(par."Subject"))
            )
            SELECT
                es."TeacherId",
                es."TeacherName",
                CAST(COUNT(DISTINCT nullif(trim(es."Grade"), '')) AS int) AS "Classes",
                CAST(COUNT(DISTINCT es."ClassId") AS int) AS "Sections",
                CAST(COUNT(DISTINCT es."SubjectKey") AS int) AS "Subjects",
                CAST(COUNT(*) AS int) AS "ExpectedPeriods",
                CAST(SUM(CASE WHEN ms."Period" IS NOT NULL THEN 1 ELSE 0 END) AS int) AS "MarkedPeriods",
                CAST(SUM(CASE WHEN ms."MarkerRole" = 'teacher' THEN 1 ELSE 0 END) AS int) AS "TeacherMarked",
                CAST(SUM(CASE WHEN ms."MarkerRole" = 'staff' THEN 1 ELSE 0 END) AS int) AS "StaffMarked",
                CAST(SUM(CASE WHEN ms."MarkerRole" = 'principal' THEN 1 ELSE 0 END) AS int) AS "PrincipalMarked",
                CAST(SUM(CASE WHEN ms."MarkerRole" = 'admin' THEN 1 ELSE 0 END) AS int) AS "AdminMarked"
            FROM "ExpectedSessions" es
            LEFT JOIN "MarkedSessions" ms
              ON ms."Date" = es."Date"
             AND ms."ClassId" = es."ClassId"
             AND ms."Period" = es."Period"
             AND ms."SubjectKey" = es."SubjectKey"
            GROUP BY es."TeacherId", es."TeacherName"
            ORDER BY es."TeacherName";
            """;

        return BuildDateRangeCommand(sql, from, to);
    }

    public static PeriodAttendanceAggregateCommand BuildRange(
        DateOnly from,
        DateOnly to,
        Guid? classId,
        string? grade,
        string? section,
        Guid? studentId,
        string? subject,
        Guid? teacherId)
    {
        ValidateRange(from, to);
        const string sql = """
            SELECT
                CAST(COALESCE(SUM(CASE WHEN par."Status" = 'present' THEN 1 ELSE 0 END), 0) AS int) AS "Present",
                CAST(COALESCE(SUM(CASE WHEN par."Status" = 'absent' THEN 1 ELSE 0 END), 0) AS int) AS "Absent",
                CAST(COALESCE(SUM(CASE WHEN par."Status" = 'late' THEN 1 ELSE 0 END), 0) AS int) AS "Late",
                CAST(COALESCE(SUM(CASE WHEN par."Status" = 'leave' THEN 1 ELSE 0 END), 0) AS int) AS "Leave"
            FROM "dbo"."PeriodAttendanceRecords" par
            INNER JOIN "dbo"."Classes" c ON c."Id" = par."ClassId"
            LEFT JOIN "dbo"."TimetableSlots" ts
              ON ts."ClassId" = par."ClassId"
             AND ts."Period" = par."Period"
             AND upper(left(trim(ts."Day"), 3))
                 = upper(left(to_char(par."Date", 'DY'), 3))
             AND lower(trim(ts."Subject")) = lower(trim(par."Subject"))
            WHERE par."Date" >= @From AND par."Date" <= @To
              AND (@ClassId::uuid IS NULL OR par."ClassId" = @ClassId::uuid)
              AND (@Grade::text IS NULL OR c."Grade" = @Grade::text)
              AND (@Section::text IS NULL OR c."Section" = @Section::text)
              AND (@StudentId::uuid IS NULL OR par."StudentId" = @StudentId::uuid)
              AND (@Subject::text IS NULL
                   OR lower(trim(par."Subject")) = lower(trim(@Subject::text)))
              AND (@TeacherId::uuid IS NULL OR ts."TeacherId" = @TeacherId::uuid);
            """;

        var parameters = DateRangeParameters(from, to);
        parameters.Add("ClassId", classId);
        parameters.Add("Grade", Clean(grade));
        parameters.Add("Section", Clean(section));
        parameters.Add("StudentId", studentId);
        parameters.Add("Subject", Clean(subject));
        parameters.Add("TeacherId", teacherId);
        return new PeriodAttendanceAggregateCommand(sql, parameters);
    }

    private static PeriodAttendanceAggregateCommand BuildDateRangeCommand(
        string sql,
        DateOnly from,
        DateOnly to,
        params (string Name, object? Value)[] extra)
    {
        var parameters = DateRangeParameters(from, to);
        foreach (var (name, value) in extra) parameters.Add(name, value);
        return new PeriodAttendanceAggregateCommand(sql, parameters);
    }

    private static DynamicParameters DateRangeParameters(DateOnly from, DateOnly to)
    {
        var parameters = new DynamicParameters();
        parameters.Add("From", from.ToDateTime(TimeOnly.MinValue));
        parameters.Add("To", to.ToDateTime(TimeOnly.MinValue));
        return parameters;
    }

    private static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (to < from) throw new ArgumentOutOfRangeException(nameof(to), "To must be on or after From.");
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record PeriodAttendanceQueryCommand(
    string Sql,
    DynamicParameters Parameters,
    int Page,
    int PageSize)
{
    public PeriodAttendanceAdvancedPage ToPage(
        IReadOnlyList<PeriodAttendanceAdvancedRow> items,
        int totalCount) =>
        new(items, totalCount, Page, PageSize);
}

public static class PeriodAttendanceQuerySql
{
    private const string FromAndJoins = """
        FROM "dbo"."PeriodAttendanceRecords" par
        INNER JOIN "dbo"."Students" s ON s."Id" = par."StudentId"
        INNER JOIN "dbo"."Classes" c ON c."Id" = par."ClassId"
        LEFT JOIN "dbo"."Users" u ON u."Id" = par."MarkedBy"
        LEFT JOIN "dbo"."Users" uu ON uu."Id" = par."UpdatedBy"
        LEFT JOIN "dbo"."TimetableSlots" ts
          ON ts."ClassId" = par."ClassId"
         AND ts."Period" = par."Period"
         AND upper(left(trim(ts."Day"), 3)) = upper(left(to_char(par."Date", 'DY'), 3))
         AND lower(trim(ts."Subject")) = lower(trim(par."Subject"))
        LEFT JOIN "dbo"."Teachers" t ON t."Id" = ts."TeacherId"
        """;

    private const string Filters = """
        WHERE par."Date" >= @From
          AND par."Date" <= @To
          AND (@ClassId IS NULL OR par."ClassId" = @ClassId)
          AND (@Grade IS NULL OR c."Grade" = @Grade)
          AND (@Section IS NULL OR c."Section" = @Section)
          AND (@Subject IS NULL OR lower(trim(par."Subject")) = lower(trim(@Subject)))
          AND (@Period IS NULL OR par."Period" = @Period)
          AND (@AssignedTeacherId IS NULL OR ts."TeacherId" = @AssignedTeacherId)
          AND (@AuthorizedTeacherId IS NULL
               OR ts."TeacherId" = @AuthorizedTeacherId
               OR c."ClassTeacherId" = @AuthorizedTeacherId)
          AND (@MarkedBy IS NULL OR par."MarkedBy" = @MarkedBy)
          AND (@MarkedByRole IS NULL
               OR replace(lower(par."MarkedByRole"), 'school.', '') = lower(@MarkedByRole))
          AND (@Status IS NULL OR par."Status" = @Status)
          AND (@GeoFenceStatus IS NULL OR COALESCE(par."GeoFenceStatus", 'not_required') = @GeoFenceStatus)
          AND (@Q IS NULL OR s."Name" LIKE '%' || @Q || '%' OR s."AdmissionNo" LIKE '%' || @Q || '%')
        """;

    private const string Sql = "SELECT CAST(COUNT(*) AS int)\n" + FromAndJoins + "\n" + Filters + ";\n" + """
        SELECT par."Id",
               par."ClassId",
               COALESCE(c."Grade", '') AS "Grade",
               COALESCE(c."Section", '') AS "Section",
               c."Name" AS "ClassLabel",
               par."StudentId",
               s."Name" AS "StudentName",
               COALESCE(s."AdmissionNo", '') AS "AdmissionNo",
               par."Date",
               par."Period",
               par."PeriodId",
               par."Subject",
               par."SubjectId",
               ts."StartTime",
               ts."EndTime",
               par."Status",
               ts."TeacherId" AS "AssignedTeacherId",
               t."Name" AS "AssignedTeacherName",
               par."MarkedBy",
               u."Name" AS "MarkedByName",
               replace(lower(par."MarkedByRole"), 'school.', '') AS "MarkedByRole",
               COALESCE(par."UpdatedAt", par."CreatedAt") AS "MarkedAt",
               COALESCE(par."GeoFenceStatus", 'not_required') AS "GeoFenceStatus",
               par."GeoDistanceMeters",
               par."GeoCapturedAt",
               par."UpdatedBy",
               uu."Name" AS "UpdatedByName",
               replace(lower(par."UpdatedByRole"), 'school.', '') AS "UpdatedByRole",
               par."UpdatedAt"
        """ + "\n" + FromAndJoins + "\n" + Filters + "\n" + """
        ORDER BY par."Date" DESC, c."Name", par."Period", s."Name"
        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
        """;

    public static PeriodAttendanceQueryCommand Build(PeriodAttendanceAdvancedQuery q)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = q.PageSize <= 0 ? 25 : Math.Clamp(q.PageSize, 1, 100);
        var parameters = new DynamicParameters();
        parameters.Add("From", q.From.ToDateTime(TimeOnly.MinValue));
        parameters.Add("To", q.To.ToDateTime(TimeOnly.MinValue));
        parameters.Add("ClassId", q.ClassId);
        parameters.Add("Grade", Clean(q.Grade));
        parameters.Add("Section", Clean(q.Section));
        parameters.Add("Subject", Clean(q.Subject));
        parameters.Add("Period", q.Period);
        parameters.Add("AssignedTeacherId", q.AssignedTeacherId);
        parameters.Add("AuthorizedTeacherId", q.AuthorizedTeacherId);
        parameters.Add("MarkedBy", q.MarkedBy);
        parameters.Add("MarkedByRole", Clean(q.MarkedByRole));
        parameters.Add("Status", Clean(q.Status));
        parameters.Add("GeoFenceStatus", Clean(q.GeoFenceStatus));
        parameters.Add("Q", Clean(q.Q));
        parameters.Add("Offset", (page - 1L) * pageSize);
        parameters.Add("PageSize", pageSize);

        return new PeriodAttendanceQueryCommand(Sql, parameters, page, pageSize);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
