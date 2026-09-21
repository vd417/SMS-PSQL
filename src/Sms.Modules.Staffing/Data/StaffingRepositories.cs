using Sms.Modules.Staffing.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Staffing.Data;

public sealed class TeacherRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string ColsBeforePhone =
        "t.\"Id\", t.\"TenantId\", t.\"Name\", t.\"Gender\", t.\"Department\", t.\"Designation\", t.\"SubjectsCsv\", t.\"ClassTeacher\", ";

    private const string ColsAfterPhone =
        "t.\"Email\", t.\"Exp\", t.\"Rating\", t.\"AttendancePct\", t.\"Result\", t.\"Load\", t.\"Status\", t.\"AvatarHue\", t.\"Top\", t.\"EmployeeCode\"";

    private const string PhoneExpr =
        "COALESCE(NULLIF(trim(t.\"Phone\"), ''), NULLIF(trim(u.\"Phone\"), ''), NULLIF(trim(peer.\"Phone\"), '')) AS \"Phone\"";

    private const string PhoneExprList =
        "COALESCE(NULLIF(trim(t.\"Phone\"), ''), NULLIF(trim(u.\"Phone\"), '')) AS \"Phone\"";

    private const string PhotoExprList = "u.\"PhotoUrl\" AS \"PhotoUrl\"";

    private const string FromJoinList =
        """
        FROM "dbo"."Teachers" t
        LEFT JOIN "dbo"."Users" u ON u."Id" = t."UserId"
        """;

    private static string TeacherSelectCols => $"{ColsBeforePhone}{PhoneExpr}, {ColsAfterPhone}";

    private const string PhotoExpr =
        "COALESCE(u.\"PhotoUrl\", peer.\"PhotoUrl\") AS \"PhotoUrl\"";

    private const string FromJoin =
        """
        FROM "dbo"."Teachers" t
        LEFT JOIN "dbo"."Users" u ON u."Id" = t."UserId"
        LEFT JOIN LATERAL (
            SELECT u2."PhotoUrl", u2."Phone"
            FROM "dbo"."Users" u2
            WHERE (u2."PhotoUrl" IS NOT NULL OR u2."Phone" IS NOT NULL)
              AND t."Email" IS NOT NULL AND u2."Email" = t."Email"
            ORDER BY CASE WHEN u2."TenantId" = t."TenantId" THEN 0 ELSE 1 END, u2."CreatedAt"
            LIMIT 1
        ) peer ON true
        """;

    public async Task<TeacherResponse?> CreateAsync(Guid tenantId, CreateTeacherRequest r, CancellationToken ct = default)
    {
        var row = await QuerySingleProcAsync<TeacherRow>("dbo.Teacher_Create", new
        {
            TenantId = tenantId, r.Name, r.Gender, r.Department, r.Designation,
            SubjectsCsv = r.Subjects is null || r.Subjects.Count == 0 ? null : string.Join(',', r.Subjects),
            r.ClassTeacher, r.Phone, r.Email, r.Exp, r.Rating, r.Result, r.Load, r.AvatarHue, r.Top,
            r.EmployeeCode,
        }, ct);
        return row?.ToResponse();
    }

    public async Task<TeacherResponse?> UpdateAsync(Guid id, UpdateTeacherRequest r, CancellationToken ct = default)
    {
        await ExecuteProcAsync("dbo.Teacher_Update", new
        {
            Id = id, r.Name, r.Department, r.Designation,
            SubjectsCsv = r.Subjects is null ? null : string.Join(',', r.Subjects),
            r.ClassTeacher, r.Phone, r.Email, r.Status, r.Gender, r.Exp, r.EmployeeCode
        }, ct);
        return null;
    }

    public async Task<TeacherResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<TeacherRow>($"SELECT {TeacherSelectCols}, {PhotoExpr}, t.\"UserId\" {FromJoin} WHERE t.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault()?.ToResponse();

    /// Null when this teacher row has never been linked to a Users row (not yet
    /// invited/accepted) — best-effort linkage, see M0084_Identity_Link_Foundation.
    public async Task<Guid?> GetUserIdAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>("SELECT \"UserId\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    /// Login user for payslip publish — UserId link, same-tenant email, then phone.
    public async Task<Guid?> ResolvePayUserIdAsync(Guid tenantId, Guid personId, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<(Guid? UserId, string? Email, string? Phone)>(
            "SELECT \"UserId\", \"Email\", \"Phone\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id = personId, tenantId }, ct)).FirstOrDefault();
        if (row.UserId is not null) return row.UserId;
        var uid = await MatchTenantUserAsync(tenantId, row.Email, row.Phone, ct);
        if (uid is not null)
        {
            await TryLinkTeacherUserIdAsync(personId, uid.Value, ct);
            return uid;
        }

        var name = (await QueryInlineAsync<string?>(
            "SELECT \"Name\" FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id = personId, tenantId }, ct)).FirstOrDefault()?.Trim();
        if (name is { Length: > 0 })
        {
            uid = (await QueryInlineAsync<Guid?>(
                "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"TenantId\" = @tenantId AND \"Name\" = @name ORDER BY \"CreatedAt\" LIMIT 1",
                new { tenantId, name }, ct)).FirstOrDefault();
            if (uid is not null)
                await TryLinkTeacherUserIdAsync(personId, uid.Value, ct);
        }
        return uid;
    }

    private async Task TryLinkTeacherUserIdAsync(Guid personId, Guid userId, CancellationToken ct) =>
        await ExecuteInlineAsync(
            "UPDATE \"dbo\".\"Teachers\" SET \"UserId\" = @userId WHERE \"Id\" = @personId AND \"UserId\" IS NULL",
            new { personId, userId }, ct);

    private async Task<Guid?> MatchTenantUserAsync(Guid tenantId, string? email, string? phone, CancellationToken ct)
    {
        var trimmedEmail = (email ?? "").Trim();
        if (trimmedEmail.Length > 0)
        {
            var byEmail = (await QueryInlineAsync<Guid?>(
                "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"TenantId\" = @tenantId AND \"Email\" = @email ORDER BY \"CreatedAt\" LIMIT 1",
                new { tenantId, email = trimmedEmail }, ct)).FirstOrDefault();
            if (byEmail is not null) return byEmail;
        }

        var trimmedPhone = (phone ?? "").Trim();
        if (trimmedPhone.Length == 0) return null;
        return (await QueryInlineAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"TenantId\" = @tenantId AND \"Phone\" = @phone ORDER BY \"CreatedAt\" LIMIT 1",
            new { tenantId, phone = trimmedPhone }, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<TeacherResponse>> ListAsync(
        string? q, string? dept, string? status, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<TeacherRow>(
            $"SELECT {ColsBeforePhone}{PhoneExprList}, {ColsAfterPhone}, {PhotoExprList}, t.\"UserId\" {FromJoinList} WHERE " +
            "(@q IS NULL OR t.\"Name\" LIKE '%' || @q || '%' OR t.\"Department\" LIKE '%' || @q || '%' OR t.\"EmployeeCode\" LIKE '%' || @q || '%') " +
            "AND (@dept IS NULL OR t.\"Department\" = @dept) AND (@status IS NULL OR t.\"Status\" = @status) ORDER BY t.\"Name\"",
            new { q, dept, status }, ct);
        return rows.Select(r => r.ToResponse()).ToList();
    }
}

public sealed class StaffRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string ColsBeforePhone =
        "s.\"Id\", s.\"TenantId\", s.\"Name\", s.\"Gender\", s.\"Role\", s.\"Category\", s.\"Department\", ";

    private const string ColsAfterPhone =
        "s.\"Shift\", s.\"Route\", s.\"AttendancePct\", s.\"Status\", s.\"AvatarHue\", s.\"EmployeeCode\", s.\"Email\"";

    private const string PhoneExpr =
        "COALESCE(NULLIF(trim(s.\"Phone\"), ''), NULLIF(trim(u.\"Phone\"), ''), NULLIF(trim(peer.\"Phone\"), '')) AS \"Phone\"";

    private const string PhoneExprList =
        "COALESCE(NULLIF(trim(s.\"Phone\"), ''), NULLIF(trim(u.\"Phone\"), '')) AS \"Phone\"";

    private static string StaffSelectCols => $"{ColsBeforePhone}{PhoneExpr}, {ColsAfterPhone}";

    private const string PhotoExpr =
        "COALESCE(u.\"PhotoUrl\", peer.\"PhotoUrl\") AS \"PhotoUrl\"";

    private const string PhotoExprList = "u.\"PhotoUrl\" AS \"PhotoUrl\"";

    private const string FromJoin =
        """
        FROM "dbo"."Staff" s
        LEFT JOIN "dbo"."Users" u ON u."Id" = s."UserId"
        LEFT JOIN LATERAL (
            SELECT u2."PhotoUrl", u2."Phone"
            FROM "dbo"."Users" u2
            WHERE (u2."PhotoUrl" IS NOT NULL OR u2."Phone" IS NOT NULL)
              AND s."Email" IS NOT NULL AND u2."Email" = s."Email"
            ORDER BY CASE WHEN u2."TenantId" = s."TenantId" THEN 0 ELSE 1 END, u2."CreatedAt"
            LIMIT 1
        ) peer ON true
        """;

    private const string FromJoinList =
        """
        FROM "dbo"."Staff" s
        LEFT JOIN "dbo"."Users" u ON u."Id" = s."UserId"
        """;

    public Task<StaffResponse?> CreateAsync(Guid tenantId, CreateStaffRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<StaffResponse>("dbo.Staff_Create", new
        {
            TenantId = tenantId, r.Name, r.Gender, r.Role, r.Category, r.Department, r.Phone, r.Shift, r.Route, r.AvatarHue,
            r.EmployeeCode, r.Email,
        }, ct);

    public async Task<StaffResponse?> UpdateAsync(Guid id, UpdateStaffRequest r, CancellationToken ct = default)
    {
        await ExecuteProcAsync("dbo.Staff_Update", new
        {
            Id = id, r.Name, r.Role, r.Category, r.Department, r.Phone, r.Shift, r.Route, r.Status, r.Email,
            r.Gender, r.EmployeeCode
        }, ct);
        return null;
    }

    public async Task<StaffResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<StaffResponse>($"SELECT {StaffSelectCols}, {PhotoExpr}, s.\"UserId\" {FromJoin} WHERE s.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    /// Null when this staff row has never been linked to a Users row (not yet
    /// invited/accepted) — best-effort linkage, see M0084_Identity_Link_Foundation.
    public async Task<Guid?> GetUserIdAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>("SELECT \"UserId\" FROM \"dbo\".\"Staff\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    /// Login user for payslip publish — UserId link, same-tenant email, then phone.
    public async Task<Guid?> ResolvePayUserIdAsync(Guid tenantId, Guid personId, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<(Guid? UserId, string? Email, string? Phone)>(
            "SELECT \"UserId\", \"Email\", \"Phone\" FROM \"dbo\".\"Staff\" WHERE \"Id\" = @id AND \"TenantId\" = @tenantId",
            new { id = personId, tenantId }, ct)).FirstOrDefault();
        if (row.UserId is not null) return row.UserId;
        var trimmedEmail = (row.Email ?? "").Trim();
        if (trimmedEmail.Length > 0)
        {
            var byEmail = (await QueryInlineAsync<Guid?>(
                "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"TenantId\" = @tenantId AND \"Email\" = @email ORDER BY \"CreatedAt\" LIMIT 1",
                new { tenantId, email = trimmedEmail }, ct)).FirstOrDefault();
            if (byEmail is not null)
            {
                await TryLinkStaffUserIdAsync(personId, byEmail.Value, ct);
                return byEmail;
            }
        }

        var trimmedPhone = (row.Phone ?? "").Trim();
        if (trimmedPhone.Length == 0) return null;
        var byPhone = (await QueryInlineAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE \"TenantId\" = @tenantId AND \"Phone\" = @phone ORDER BY \"CreatedAt\" LIMIT 1",
            new { tenantId, phone = trimmedPhone }, ct)).FirstOrDefault();
        if (byPhone is not null)
            await TryLinkStaffUserIdAsync(personId, byPhone.Value, ct);
        return byPhone;
    }

    private async Task TryLinkStaffUserIdAsync(Guid personId, Guid userId, CancellationToken ct) =>
        await ExecuteInlineAsync(
            "UPDATE \"dbo\".\"Staff\" SET \"UserId\" = @userId WHERE \"Id\" = @personId AND \"UserId\" IS NULL",
            new { personId, userId }, ct);

    public Task<IReadOnlyList<StaffResponse>> ListAsync(string? q, string? cat, CancellationToken ct = default) =>
        QueryInlineAsync<StaffResponse>(
            $"SELECT {ColsBeforePhone}{PhoneExprList}, {ColsAfterPhone}, {PhotoExprList}, s.\"UserId\" {FromJoinList} WHERE " +
            "(@q IS NULL OR s.\"Name\" LIKE '%' || @q || '%' OR s.\"Role\" LIKE '%' || @q || '%' OR s.\"EmployeeCode\" LIKE '%' || @q || '%') " +
            "AND (@cat IS NULL OR s.\"Category\" = @cat) ORDER BY s.\"Name\"",
            new { q, cat }, ct);

    /// The caller's own Category (e.g. "driver"/"conductor") for the staff dashboard's role
    /// card — resolved from Staff.UserId, same identity join as everywhere else self-service.
    public async Task<string?> GetCategoryByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryInlineAsync<string?>(
            "SELECT \"Category\" FROM \"dbo\".\"Staff\" WHERE \"UserId\" = @userId", new { userId }, ct)).FirstOrDefault();
}
