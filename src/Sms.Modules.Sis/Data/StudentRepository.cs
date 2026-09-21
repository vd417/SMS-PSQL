using Sms.Modules.Sis.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Sis.Data;

public sealed class StudentRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Gender\", \"Grade\", \"Section\", \"ClassLabel\", \"Roll\", \"GuardianName\", " +
        "\"GuardianPhone\", \"GuardianEmail\", \"AttendancePct\", \"FeeStatus\", \"FeeDue\", \"Status\", \"House\", \"AvatarHue\", \"Dob\", \"Email\", \"Address\", \"PhotoUrl\"";

    /// <summary>
    /// Official AttendancePct from PeriodAttendanceRecords only:
    /// (present + late) / marked periods × 100. NULL when unmarked (not 0%).
    /// Legacy daily AttendanceRecords are excluded.
    /// List uses one grouped join (not a correlated apply per student).
    /// </summary>
    private const string LivePctSelect = """
s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Gender", s."Grade", s."Section", s."ClassLabel", s."Roll",
s."GuardianName", s."GuardianPhone", s."GuardianEmail",
CAST(CASE WHEN att."Marked" > 0
          THEN ROUND(100.0 * att."Positive" / att."Marked", 2)
          ELSE NULL END AS numeric(5,2)) AS "AttendancePct",
s."FeeStatus", s."FeeDue", s."Status", s."House", s."AvatarHue", s."Dob", s."Email", s."Address", s."PhotoUrl"
""";

    private const string LivePctFromList = """
FROM "dbo"."Students" s
LEFT JOIN (
    SELECT par."StudentId",
           count(*) AS "Marked",
           sum(CASE WHEN par."Status" IN ('present', 'late') THEN 1 ELSE 0 END) AS "Positive"
    FROM "dbo"."PeriodAttendanceRecords" par
    WHERE (@tenantId IS NULL OR par."TenantId" = @tenantId)
    GROUP BY par."StudentId"
) att ON att."StudentId" = s."Id"
""";

    private const string ListWhere = """
WHERE (@q IS NULL OR s."Name" LIKE '%' || @q || '%' OR s."AdmissionNo" LIKE '%' || @q || '%' OR s."ClassLabel" LIKE '%' || @q || '%')
  AND (@grade IS NULL OR s."Grade" = @grade) AND (@status IS NULL OR s."Status" = @status)
  AND (@fee IS NULL OR s."FeeStatus" = @fee)
""";

    private const string LivePctFromOne = """
FROM "dbo"."Students" s
LEFT JOIN LATERAL (
    SELECT count(*) AS "Marked",
           sum(CASE WHEN par."Status" IN ('present', 'late') THEN 1 ELSE 0 END) AS "Positive"
    FROM "dbo"."PeriodAttendanceRecords" par
    WHERE par."StudentId" = s."Id"
) att ON true
""";

    public Task<StudentResponse?> CreateAsync(Guid tenantId, CreateStudentRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<StudentResponse>("dbo.Student_Create", new
        {
            TenantId = tenantId, r.AdmissionNo, r.Name, r.Gender, r.Grade, r.Section, r.Roll,
            r.GuardianName, r.GuardianPhone, r.GuardianEmail, r.House, r.AvatarHue, r.Dob, r.Email, r.Address
        }, ct);

    public Task<StudentResponse?> UpdateAsync(Guid id, UpdateStudentRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<StudentResponse>("dbo.Student_Update", new
        {
            Id = id, r.Name, r.Grade, r.Section, r.Roll, r.GuardianName, r.GuardianPhone, r.GuardianEmail,
            r.House, r.FeeStatus, r.FeeDue, r.Status, r.PhotoUrl, r.SetPhoto,
            r.Gender, r.Dob, r.Email, r.Address, r.AvatarHue
        }, ct);

    public async Task<StudentResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<StudentResponse>($"SELECT {LivePctSelect} {LivePctFromOne} WHERE s.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public async Task<StudentResponse?> GetByAdmissionNoAsync(string admissionNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(admissionNo)) return null;
        return (await QueryInlineAsync<StudentResponse>(
            $"SELECT {LivePctSelect} {LivePctFromOne} WHERE lower(trim(s.\"AdmissionNo\")) = lower(trim(@admissionNo))",
            new { admissionNo = admissionNo.Trim() }, ct)).FirstOrDefault();
    }

    /// Roster rows a parent can see: ParentStudentLinks for this user and tenant only.
    public Task<IReadOnlyList<StudentResponse>> ListLinkedToParentAsync(
        Guid parentUserId, Guid tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<StudentResponse>(
            $"SELECT {LivePctSelect} {LivePctFromOne} WHERE s.\"TenantId\" = @tenantId AND EXISTS (" +
            "SELECT 1 FROM \"dbo\".\"ParentStudentLinks\" l " +
            "WHERE l.\"ParentUserId\" = @parentUserId AND l.\"StudentId\" = s.\"Id\" AND l.\"TenantId\" = @tenantId" +
            ") ORDER BY s.\"Name\", s.\"Id\"",
            new { parentUserId, tenantId }, ct);

    public Task<IReadOnlyList<Guid>> ListParentUserIdsAsync(
        Guid studentId, string admissionNo, CancellationToken ct = default) =>
        QueryInlineAsync<Guid>("""
SELECT DISTINCT "Id" FROM (
    SELECT "ParentUserId" AS "Id" FROM "dbo"."ParentStudentLinks" WHERE "StudentId" = @studentId
    UNION
    SELECT u."Id" FROM "dbo"."Users" u
    WHERE @admissionNo IS NOT NULL AND trim(@admissionNo) <> '' AND u."StudentId" = @admissionNo
) p
""", new { studentId, admissionNo }, ct);

    public async Task SetGuardianEmailAsync(Guid id, string email, CancellationToken ct = default) =>
        await ExecuteInlineAsync(
            """UPDATE "dbo"."Students" SET "GuardianEmail" = @email WHERE "Id" = @id""",
            new { id, email }, ct);

    public async Task SetGuardianContactAsync(
        Guid id, string? email, string? phone, string? name, CancellationToken ct = default) =>
        await ExecuteInlineAsync(
            """
            UPDATE "dbo"."Students" SET
                "GuardianEmail" = COALESCE(@email, "GuardianEmail"),
                "GuardianPhone" = COALESCE(@phone, "GuardianPhone"),
                "GuardianName"  = COALESCE(@name, "GuardianName")
            WHERE "Id" = @id
            """,
            new { id, email, phone, name }, ct);

    public async Task<IReadOnlyList<StudentResponse>> ListAsync(
        string? q, string? grade, string? status, string? fee, CancellationToken ct = default)
    {
        var (rows, _) = await ListPagedAsync(q, grade, status, fee, ct);
        return rows;
    }

    /// <summary>
    /// When <paramref name="limit"/> is null, returns the full filtered roster (mobile contract).
    /// When set, SQL keyset-pages on (Name, Id); max 100 rows.
    /// </summary>
    public async Task<(IReadOnlyList<StudentResponse> Rows, string? NextCursor)> ListPagedAsync(
        string? q, string? grade, string? status, string? fee,
        CancellationToken ct = default,
        Guid? tenantId = null,
        int? limit = null,
        string? cursor = null)
    {
        string? lastName = null;
        Guid? lastId = null;
        int? take = null;
        if (limit is int raw)
        {
            take = Math.Clamp(raw, 1, 100);
            var decoded = Sms.Shared.Kernel.Http.Cursor.Decode(cursor);
            if (decoded is not null)
            {
                var i = decoded.IndexOf('|');
                if (i > 0 && Guid.TryParse(decoded[(i + 1)..], out var g))
                {
                    lastName = decoded[..i];
                    lastId = g;
                }
            }
        }

        var sql = take is int
            ? $"""
               SELECT {LivePctSelect} {LivePctFromList} {ListWhere}
                 AND (@lastName IS NULL OR s."Name" > @lastName OR (s."Name" = @lastName AND s."Id" > @lastId))
                 ORDER BY s."Name", s."Id" LIMIT @limit
               """
            : $"SELECT {LivePctSelect} {LivePctFromList} {ListWhere} ORDER BY s.\"Name\", s.\"Id\"";

        var rows = await QueryInlineAsync<StudentResponse>(
            sql,
            new { tenantId, q, grade, status, fee, limit = take, lastName, lastId },
            ct);

        string? next = take is int pageSize && rows.Count == pageSize
            ? Sms.Shared.Kernel.Http.Cursor.Encode($"{rows[^1].Name}|{rows[^1].Id}")
            : null;
        return (rows, next);
    }

    /// Students belonging to a class, matched by the class's Grade+Section (no ClassId exists).
    /// Keyset paginated on (Name, Id). Returns up to `limit` rows and a NextCursor when a full page returns.
    public async Task<(IReadOnlyList<StudentResponse> Rows, string? NextCursor)> ListByClassPagedAsync(
        Guid classId, int limit, string? cursor, CancellationToken ct = default, Guid? tenantId = null)
    {
        string? lastName = null; Guid? lastId = null;
        var decoded = Sms.Shared.Kernel.Http.Cursor.Decode(cursor);
        if (decoded is not null)
        {
            var i = decoded.IndexOf('|');
            if (i > 0 && Guid.TryParse(decoded[(i + 1)..], out var g)) { lastName = decoded[..i]; lastId = g; }
        }

        var rows = await QueryInlineAsync<StudentResponse>(
            $"""
             SELECT {LivePctSelect} {LivePctFromList}
               WHERE EXISTS (SELECT 1 FROM "dbo"."Classes" c
                             WHERE c."Id" = @classId AND c."Grade" = s."Grade" AND c."Section" = s."Section")
                 AND (@lastName IS NULL OR s."Name" > @lastName
                      OR (s."Name" = @lastName AND s."Id" > @lastId))
               ORDER BY s."Name", s."Id" LIMIT @limit
             """,
            new { classId, limit, lastName, lastId, tenantId }, ct);

        string? next = rows.Count == limit
            ? Sms.Shared.Kernel.Http.Cursor.Encode($"{rows[^1].Name}|{rows[^1].Id}")
            : null;
        return (rows, next);
    }
}
