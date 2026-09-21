using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tasks;

public static class TaskEnums
{
    public static readonly string[] ValidPriorities = ["urgent", "normal"];
    public static readonly string[] ValidStatuses = ["pending", "in_progress", "completed"];

    /// The six canonical duty-role keys the staff app understands (same set StaffRoleMapper
    /// produces from a Staff row's free-text Designation).
    public static readonly string[] ValidRoleKeys = ["driver", "conductor", "sweeper", "gardener", "guard", "peon"];
}

/// Full task row, used for manager create/list-all responses. The three *Name fields are
/// resolved via LEFT JOIN against dbo.Users — null whenever the corresponding *UserId is null
/// (e.g. CompletedByUserId before completion); never a reason for the task itself to disappear
/// from a list. Stored-proc-backed methods (Create/Complete/AttachPhoto) don't select these
/// joined columns, so they come back null there too — acceptable since those responses are
/// either consumed by the mobile app (which ignores them) or immediately followed by a list
/// refetch on the CRM side.
public sealed record TaskResponse(
    Guid Id, Guid TenantId, string Title, string? Detail, string? Category,
    Guid? AssignedToUserId, string? AssignedToRoleKey, string Priority, string Status,
    DateTime? DueDate, string? Remarks, string? PhotoUrl,
    Guid CreatedByUserId, Guid? CompletedByUserId, DateTime CreatedAt, DateTime? CompletedAt,
    string? AssignedToUserName = null, string? CreatedByUserName = null, string? CompletedByUserName = null)
{
    /// Dapper maps `Task_Create`/`Task_Complete`/`Task_AttachPhoto` from a 16-column SELECT that
    /// does not include the joined *Name fields. Optional parameters on the primary constructor
    /// still produce a 19-parameter ctor, which Dapper will not match to those 16 columns.
    public TaskResponse(
        Guid Id, Guid TenantId, string Title, string? Detail, string? Category,
        Guid? AssignedToUserId, string? AssignedToRoleKey, string Priority, string Status,
        DateTime? DueDate, string? Remarks, string? PhotoUrl,
        Guid CreatedByUserId, Guid? CompletedByUserId, DateTime CreatedAt, DateTime? CompletedAt)
        : this(Id, TenantId, Title, Detail, Category, AssignedToUserId, AssignedToRoleKey,
            Priority, Status, DueDate, Remarks, PhotoUrl, CreatedByUserId, CompletedByUserId,
            CreatedAt, CompletedAt, null, null, null)
    {
    }
}

public sealed record CreateTaskRequest(
    string Title, string? Detail, string? Category, string Priority,
    DateTime? DueDate, Guid? AssignedToUserId, string? AssignedToRoleKey);

/// Optional server-side filters for the manager "list all" view. Every field left null/empty
/// preserves the endpoint's original unfiltered behavior. Cursor is an opaque keyset token from
/// a prior page's NextCursor (see TaskCursor) — never an OFFSET.
public sealed record TaskListFilter(
    string? Status = null, Guid? AssignedToUserId = null, string? AssignedToRoleKey = null,
    DateTime? From = null, DateTime? To = null, string? Cursor = null);

/// Encodes/decodes the (CreatedAt, Id) keyset cursor used by ListAllAsync. Opaque to callers —
/// they only ever pass back a NextCursor they were handed, never construct one.
public static class TaskCursor
{
    public static string Encode(DateTime createdAt, Guid id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{createdAt:O}|{id}"));

    /// Returns null for a null/blank/malformed cursor — treated the same as "no cursor" (first
    /// page) rather than as an error, since a stale or tampered token should just restart paging.
    public static (DateTime CreatedAt, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            if (parts.Length != 2) return null;
            if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt))
                return null;
            if (!Guid.TryParse(parts[1], out var id)) return null;
            return (createdAt, id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed class TaskRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string TaskCols =
        "t.\"Id\", t.\"TenantId\", t.\"Title\", t.\"Detail\", t.\"Category\", t.\"AssignedToUserId\", t.\"AssignedToRoleKey\", " +
        "t.\"Priority\", t.\"Status\", t.\"DueDate\", t.\"Remarks\", t.\"PhotoUrl\", t.\"CreatedByUserId\", t.\"CompletedByUserId\", " +
        "t.\"CreatedAt\", t.\"CompletedAt\", au.\"Name\" AS \"AssignedToUserName\", cu.\"Name\" AS \"CreatedByUserName\", " +
        "xu.\"Name\" AS \"CompletedByUserName\"";

    private const string TaskFromJoin =
        """
        FROM "dbo"."Tasks" t
        LEFT JOIN "dbo"."Users" au ON au."Id" = t."AssignedToUserId"
        LEFT JOIN "dbo"."Users" cu ON cu."Id" = t."CreatedByUserId"
        LEFT JOIN "dbo"."Users" xu ON xu."Id" = t."CompletedByUserId"
        """;

    /// Page size for ListAllAsync: one row over this is fetched so the caller can tell whether
    /// a next page exists without a separate COUNT query.
    private const int ListAllPageSize = 200;

    private sealed record RoleRow(string? Role);

    public Task<IReadOnlyList<TaskResponse>> ListForCallerAsync(
        Guid userId, string? roleKey, CancellationToken ct = default) =>
        QueryInlineAsync<TaskResponse>(
            $"SELECT {TaskCols} {TaskFromJoin} " +
            "WHERE t.\"AssignedToUserId\" = @userId " +
            "   OR (t.\"AssignedToRoleKey\" = @roleKey::text AND t.\"AssignedToUserId\" IS NULL AND @roleKey::text IS NOT NULL) " +
            "ORDER BY t.\"CreatedAt\" DESC",
            new { userId, roleKey }, ct);

    /// Manager tenant-wide list, with optional filters and real keyset pagination on
    /// (CreatedAt DESC, Id DESC). Fetches one row beyond the page size to detect whether a next
    /// page exists (no separate COUNT query), trims it, and encodes NextCursor from the last row
    /// actually returned — so a bounded query (e.g. "this week") that fits in one page comes back
    /// with NextCursor null, exactly like a non-paginated response.
    public async Task<(IReadOnlyList<TaskResponse> Rows, string? NextCursor)> ListAllAsync(
        TaskListFilter filter, CancellationToken ct = default)
    {
        var cursor = TaskCursor.Decode(filter.Cursor);
        var rows = await QueryInlineAsync<TaskResponse>(
            $"""
            SELECT {TaskCols}
            {TaskFromJoin}
            WHERE (@status::text IS NULL OR t."Status" = @status::text)
              AND (@assignedToUserId::uuid IS NULL OR t."AssignedToUserId" = @assignedToUserId::uuid)
              AND (@assignedToRoleKey::text IS NULL OR t."AssignedToRoleKey" = @assignedToRoleKey::text)
              AND (@from::timestamptz IS NULL OR t."CreatedAt" >= @from::timestamptz)
              AND (@to::timestamptz IS NULL OR t."CreatedAt" <= @to::timestamptz)
              AND (@cursorCreatedAt::timestamptz IS NULL
                   OR t."CreatedAt" < @cursorCreatedAt::timestamptz
                   OR (t."CreatedAt" = @cursorCreatedAt::timestamptz AND t."Id" < @cursorId::uuid))
            ORDER BY t."CreatedAt" DESC, t."Id" DESC
            LIMIT @limit
            """,
            new
            {
                limit = ListAllPageSize + 1,
                status = filter.Status,
                assignedToUserId = filter.AssignedToUserId,
                assignedToRoleKey = filter.AssignedToRoleKey,
                from = filter.From,
                to = filter.To,
                cursorCreatedAt = cursor?.CreatedAt,
                cursorId = cursor?.Id,
            }, ct);

        if (rows.Count <= ListAllPageSize) return (rows, null);
        var page = rows.Take(ListAllPageSize).ToList();
        var last = page[^1];
        return (page, TaskCursor.Encode(last.CreatedAt, last.Id));
    }

    public async Task<TaskResponse?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await QueryInlineAsync<TaskResponse>($"SELECT {TaskCols} {TaskFromJoin} WHERE t.\"Id\" = @id", new { id }, ct))
        .FirstOrDefault();

    public Task<TaskResponse?> CreateAsync(
        Guid tenantId, Guid createdByUserId, CreateTaskRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<TaskResponse>("dbo.Task_Create", new
        {
            TenantId = tenantId,
            CreatedByUserId = createdByUserId,
            r.Title,
            r.Detail,
            r.Category,
            r.AssignedToUserId,
            r.AssignedToRoleKey,
            r.Priority,
            r.DueDate,
        }, ct);

    public Task<TaskResponse?> CompleteAsync(Guid id, Guid completedByUserId, CancellationToken ct = default) =>
        QuerySingleProcAsync<TaskResponse>("dbo.Task_Complete", new { Id = id, CompletedByUserId = completedByUserId }, ct);

    public Task<TaskResponse?> AttachPhotoAsync(Guid id, string? photoUrl, CancellationToken ct = default) =>
        QuerySingleProcAsync<TaskResponse>("dbo.Task_AttachPhoto", new { Id = id, PhotoUrl = photoUrl }, ct);

    /// The caller's canonical duty role_key, derived the same way /auth/me computes it:
    /// the caller's own linked dbo.Staff row's free-text Role, normalized via StaffRoleMapper.
    /// Null for non-staff users (teachers, admins, parents, students) who have no Staff row.
    public async Task<string?> GetCallerRoleKeyAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<RoleRow>(
            "SELECT \"Role\" FROM \"dbo\".\"Staff\" WHERE \"UserId\" = @userId LIMIT 1", new { userId }, ct);
        return StaffRoleMapper.ToRoleKey(rows.FirstOrDefault()?.Role);
    }

    /// SQL mirror of StaffRoleMapper.ToRoleKey — duplicated deliberately because this repository
    /// aggregates across every duty-role staff member in one round trip (a per-row C# call would
    /// mean loading the whole roster just to re-filter it). Keep in exact sync with
    /// StaffRoleMapper.ToRoleKey by hand;
    /// TaskSummaryEndpointTests.People_summary_role_key_matches_StaffRoleMapper_for_every_recognized_designation
    /// asserts every input StaffRoleMapper recognizes maps identically here.
    private const string RoleKeyCase =
        """
        CASE lower(trim(s."Role"))
          WHEN 'driver' THEN 'driver'
          WHEN 'conductor' THEN 'conductor'
          WHEN 'bus attendant' THEN 'conductor'
          WHEN 'watchman' THEN 'guard'
          WHEN 'security guard' THEN 'guard'
          WHEN 'peon' THEN 'peon'
          WHEN 'sweeper' THEN 'sweeper'
          WHEN 'gardener' THEN 'gardener'
          ELSE NULL
        END
        """;

    /// Staff linked to a login (UserId not null), with their canonical duty role_key (null if
    /// their free-text Role doesn't map to one of the six). Shared CTE for both summary queries.
    private const string DutyStaffCte =
        $"""
        DutyStaff AS (
            SELECT s."UserId" AS "UserId", s."Name" AS "Name", {RoleKeyCase} AS "RoleKey"
            FROM "dbo"."Staff" s
            WHERE s."UserId" IS NOT NULL
        )
        """;

    /// Per-person task counts: every duty-role staff member (role_key in the canonical six,
    /// shown even with zero tasks) UNION any other staff member directly assigned >= 1 task.
    /// A person's totals include both tasks assigned to them by name and — for duty-role staff —
    /// broadcast tasks assigned to their whole role, exactly like GetCallerRoleKeyAsync/
    /// ListForCallerAsync's "my tasks" semantics. Overdue = Status != 'completed' AND
    /// DueDate < today (computed, never stored). LastActivityAt = the later of "this task was
    /// assigned to me" (CreatedAt) or "I personally completed this task" (CompletedAt attributed
    /// only to the actual completer, not every role-holder on a broadcast task).
    public Task<IReadOnlyList<PersonTaskSummary>> ListPeopleSummaryAsync(
        DateTime today, CancellationToken ct = default) =>
        QueryInlineAsync<PersonTaskSummary>(
            $"""
            WITH {DutyStaffCte},
            Eligible AS (
                SELECT "UserId", "Name", "RoleKey" FROM DutyStaff WHERE "RoleKey" IS NOT NULL
                UNION
                SELECT t."AssignedToUserId" AS "UserId", u."Name" AS "Name", ds."RoleKey" AS "RoleKey"
                FROM "dbo"."Tasks" t
                JOIN "dbo"."Users" u ON u."Id" = t."AssignedToUserId"
                LEFT JOIN DutyStaff ds ON ds."UserId" = t."AssignedToUserId"
                WHERE t."AssignedToUserId" IS NOT NULL
            )
            SELECT
                e."UserId", e."Name", e."RoleKey",
                CAST(COUNT(m."Id") AS int) AS "TotalTasks",
                CAST(SUM(CASE WHEN m."Status" = 'pending' THEN 1 ELSE 0 END) AS int) AS "PendingTasks",
                CAST(SUM(CASE WHEN m."Status" = 'completed' THEN 1 ELSE 0 END) AS int) AS "CompletedTasks",
                CAST(SUM(CASE WHEN m."Status" <> 'completed' AND m."DueDate" IS NOT NULL AND m."DueDate" < @today
                         THEN 1 ELSE 0 END) AS int) AS "OverdueTasks",
                MAX(CASE WHEN m."CompletedByUserId" = e."UserId" THEN m."CompletedAt" ELSE m."CreatedAt" END) AS "LastActivityAt"
            FROM Eligible e
            LEFT JOIN "dbo"."Tasks" m
                ON m."AssignedToUserId" = e."UserId"
                OR (m."AssignedToRoleKey" = e."RoleKey" AND m."AssignedToUserId" IS NULL AND e."RoleKey" IS NOT NULL)
            GROUP BY e."UserId", e."Name", e."RoleKey"
            ORDER BY e."Name"
            """,
            new { today }, ct);

    /// One row per canonical duty-role key (always exactly six, even a role with no staff or no
    /// tasks yet), aggregating every task touching a member of that role — both broadcasts to the
    /// role and tasks assigned to a specific person who holds it. Same Overdue/LastActivityAt
    /// rules as ListPeopleSummaryAsync.
    public Task<IReadOnlyList<RoleTaskSummary>> ListRoleSummaryAsync(
        DateTime today, CancellationToken ct = default) =>
        QueryInlineAsync<RoleTaskSummary>(
            $"""
            WITH {DutyStaffCte}
            SELECT
                rk."RoleKey",
                CAST((SELECT COUNT(*) FROM DutyStaff ds WHERE ds."RoleKey" = rk."RoleKey") AS int) AS "Headcount",
                CAST(COUNT(m."Id") AS int) AS "TotalTasks",
                CAST(SUM(CASE WHEN m."Status" = 'pending' THEN 1 ELSE 0 END) AS int) AS "PendingTasks",
                CAST(SUM(CASE WHEN m."Status" = 'completed' THEN 1 ELSE 0 END) AS int) AS "CompletedTasks",
                CAST(SUM(CASE WHEN m."Status" <> 'completed' AND m."DueDate" IS NOT NULL AND m."DueDate" < @today
                         THEN 1 ELSE 0 END) AS int) AS "OverdueTasks",
                MAX(COALESCE(m."CompletedAt", m."CreatedAt")) AS "LastActivityAt"
            FROM (VALUES ('driver'), ('conductor'), ('sweeper'), ('gardener'), ('guard'), ('peon')) AS rk("RoleKey")
            LEFT JOIN "dbo"."Tasks" m
                ON m."AssignedToRoleKey" = rk."RoleKey"
                OR m."AssignedToUserId" IN (SELECT "UserId" FROM DutyStaff WHERE "RoleKey" = rk."RoleKey")
            GROUP BY rk."RoleKey"
            """,
            new { today }, ct);
}

/// Per-person aggregate for the Task Management "People" view. RoleKey is null for a staff
/// member who was directly assigned a task but doesn't hold one of the six canonical duty roles.
public sealed record PersonTaskSummary(
    Guid UserId, string Name, string? RoleKey,
    int TotalTasks, int PendingTasks, int CompletedTasks, int OverdueTasks, DateTime? LastActivityAt);

/// Per-role aggregate for the Task Management "Roles" view — always exactly the six canonical
/// TaskEnums.ValidRoleKeys, headcount and task counts tenant-scoped via RLS on the underlying joins.
public sealed record RoleTaskSummary(
    string RoleKey, int Headcount, int TotalTasks, int PendingTasks, int CompletedTasks,
    int OverdueTasks, DateTime? LastActivityAt);

public static class TaskModule
{
    public static IServiceCollection AddTasksModule(this IServiceCollection services)
    {
        services.AddScoped<TaskRepository>();
        return services;
    }
}
