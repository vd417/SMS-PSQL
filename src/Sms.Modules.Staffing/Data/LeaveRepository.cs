using Sms.Modules.Staffing.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Staffing.Data;

public sealed class LeaveRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Cols =
        "\"Id\", \"TenantId\", \"RequesterId\", \"ChildId\", \"Type\", \"FromDate\", \"ToDate\", \"Reason\", \"Substitute\", \"Status\", \"AppliedOn\", \"DecidedNote\", \"Priority\", \"AttachmentUrls\"";

    public Task<LeaveResponse?> CreateAsync(
        Guid tenantId, Guid? requesterId, CreateLeaveRequest r, string? attachmentUrlsJson, CancellationToken ct = default) =>
        QuerySingleProcAsync<LeaveResponse>("dbo.Leave_Create", new
        {
            TenantId = tenantId, RequesterId = requesterId, r.ChildId, r.Type, r.FromDate, r.ToDate, r.Reason, r.Substitute,
            Priority = r.Priority ?? "medium", AttachmentUrls = attachmentUrlsJson
        }, ct);

    public Task<LeaveResponse?> DecideAsync(Guid id, string status, Guid? decidedBy, string? note, CancellationToken ct = default) =>
        QuerySingleProcAsync<LeaveResponse>("dbo.Leave_Decide",
            new { Id = id, Status = status, DecidedBy = decidedBy, DecidedNote = note }, ct);

    public async Task<LeaveResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<LeaveResponse>($"SELECT {Cols} FROM \"dbo\".\"LeaveRequests\" WHERE \"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<IReadOnlyList<LeaveBalanceResponse>> GetBalancesAsync(
        Guid tenantId, Guid? requesterId, int year, CancellationToken ct = default) =>
        QueryProcAsync<LeaveBalanceResponse>("dbo.Leave_Balances",
            new { TenantId = tenantId, RequesterId = requesterId, Year = year }, ct);

    public Task<IReadOnlyList<LeaveResponse>> ListMineAsync(Guid? requesterId, CancellationToken ct = default) =>
        QueryInlineAsync<LeaveResponse>(
            $"SELECT {Cols} FROM \"dbo\".\"LeaveRequests\" WHERE \"RequesterId\" = @requesterId ORDER BY \"AppliedOn\" DESC",
            new { requesterId }, ct);

    /// Active students in the classes the given teacher user is ClassTeacherId of.
    public Task<IReadOnlyList<Guid>> StudentIdsForClassTeacherAsync(
        Guid teacherUserId, Guid tenantId, CancellationToken ct = default) =>
        QueryInlineAsync<Guid>("""
            SELECT s."Id"
            FROM "dbo"."Teachers" t
            JOIN "dbo"."Classes" c ON c."ClassTeacherId" = t."Id"
            JOIN "dbo"."Students" s ON (
                (c."Grade" IS NOT NULL AND c."Section" IS NOT NULL
                   AND s."Grade" = c."Grade" AND s."Section" = c."Section")
                OR (c."Name" IS NOT NULL AND s."ClassLabel" = c."Name"))
            WHERE t."UserId" = @teacherUserId
              AND s."Status" = 'active'
              AND s."TenantId" = @tenantId
            """, new { teacherUserId, tenantId }, ct);

    public Task<IReadOnlyList<LeaveResponse>> ListByStatusAsync(
        string? status, IReadOnlyCollection<Guid>? childScope = null, CancellationToken ct = default)
    {
        var all = string.IsNullOrWhiteSpace(status)
            || status.Equals("all", StringComparison.OrdinalIgnoreCase);
        const string from = """
            FROM "dbo"."LeaveRequests" lr
            LEFT JOIN "dbo"."Users" u ON u."Id" = lr."RequesterId"
            LEFT JOIN "dbo"."Users" d ON d."Id" = lr."DecidedBy"
            LEFT JOIN "dbo"."Students" st ON st."Id" = lr."ChildId" AND st."TenantId" = lr."TenantId"
            """;
        // Scalar subquery, not a JOIN — UserRoles' key is (UserId, Role), so a user with more
        // than one role would fan-out a plain join and duplicate the leave row per role.
        const string select = """
            SELECT lr."Id", lr."TenantId", lr."RequesterId", lr."ChildId", lr."Type", lr."FromDate", lr."ToDate",
                   lr."Reason", lr."Substitute", lr."Status", lr."AppliedOn", lr."DecidedNote", lr."Priority", lr."AttachmentUrls",
                   u."Name" AS "RequesterName", d."Name" AS "DecidedByName",
                   (SELECT "Role" FROM "dbo"."UserRoles" WHERE "UserId" = lr."RequesterId" ORDER BY "Role" LIMIT 1) AS "RequesterRole",
                   st."Name" AS "StudentName", st."AdmissionNo" AS "AdmissionNo",
                   COALESCE(st."ClassLabel", st."Grade") AS "StudentClass",
                   st."Section" AS "StudentSection", st."Roll" AS "StudentRoll"
            """;
        // childScope non-null => restrict to those child ids (also drops null-ChildId staff leave).
        // An empty scope matches nothing (= ANY('{}')); it is NOT "no filter".
        var conds = new List<string>();
        if (!all) conds.Add("lr.\"Status\" = @status");
        if (childScope is not null) conds.Add("lr.\"ChildId\" = ANY(@childScope)");
        var where = conds.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conds);
        var sql = $"{select} {from}{where} ORDER BY lr.\"AppliedOn\" DESC";
        return QueryInlineAsync<LeaveResponse>(sql,
            new { status = all ? null : status, childScope = childScope?.ToArray() }, ct);
    }
}
