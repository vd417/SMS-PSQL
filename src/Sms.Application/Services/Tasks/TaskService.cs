using System.Security.Claims;
using Sms.Application.Common;
using Sms.Modules.Tasks;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Http;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Application.Services.Tasks;

/// Wire shape the staff app's httpTasks repository expects verbatim (see
/// sms-staff/src/data/http/mappers.ts TaskDTO): id, title, detail?, priority, done, due_label?,
/// photo_url?. All three staff endpoints (list/complete/photo) return the caller's full,
/// refreshed task list in this shape, never a single mutated row.
public sealed record StaffTaskDto(
    Guid Id, string Title, string? Detail, string Priority, bool Done, string? DueLabel, string? PhotoUrl,
    string? Remarks);

public interface ITaskService
{
    Task<ApiResult<IReadOnlyList<StaffTaskDto>>> ListMineAsync(ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<StaffTaskDto>>> CompleteAsync(Guid id, string? remark, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<StaffTaskDto>>> AttachPhotoAsync(
        Guid id, string? photoBase64, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<TaskResponse>> CreateAsync(CreateTaskRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<CursorPage<TaskResponse>>> ListAllAsync(
        TaskListFilter filter, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PersonTaskSummary>>> ListPeopleSummaryAsync(
        ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<RoleTaskSummary>>> ListRoleSummaryAsync(
        ClaimsPrincipal caller, CancellationToken ct = default);
}

public sealed class TaskService(TaskRepository repo, ITenantContext tenant, IClock clock) : ITaskService
{
    public async Task<ApiResult<IReadOnlyList<StaffTaskDto>>> ListMineAsync(
        ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(new Error("forbidden", "no user context"), 403);
        return ApiResult<IReadOnlyList<StaffTaskDto>>.Ok(await MineAsync(uid, ct));
    }

    public async Task<ApiResult<IReadOnlyList<StaffTaskDto>>> CompleteAsync(
        Guid id, string? remark, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(new Error("forbidden", "no user context"), 403);

        var authz = await AuthorizeAssignedAsync(id, uid, ct);
        if (authz.Error is { } error)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(error, authz.StatusCode);

        await repo.CompleteAsync(id, uid, string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(), ct);
        return ApiResult<IReadOnlyList<StaffTaskDto>>.Ok(await MineAsync(uid, ct));
    }

    public async Task<ApiResult<IReadOnlyList<StaffTaskDto>>> AttachPhotoAsync(
        Guid id, string? photoBase64, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(new Error("forbidden", "no user context"), 403);
        if (ImageUrlValidation.Validate(photoBase64) is { } photoError)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(photoError, 400);

        var authz = await AuthorizeAssignedAsync(id, uid, ct);
        if (authz.Error is { } error)
            return ApiResult<IReadOnlyList<StaffTaskDto>>.Fail(error, authz.StatusCode);

        await repo.AttachPhotoAsync(id, ImageUrlValidation.Normalize(photoBase64), ct);
        return ApiResult<IReadOnlyList<StaffTaskDto>>.Ok(await MineAsync(uid, ct));
    }

    public async Task<ApiResult<TaskResponse>> CreateAsync(
        CreateTaskRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!RoleChecks.IsTaskManager(caller))
            return ApiResult<TaskResponse>.Fail(new Error("forbidden", "manager only"), 403);
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<TaskResponse>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (string.IsNullOrWhiteSpace(req.Title))
            return ApiResult<TaskResponse>.Fail(new Error("invalid_request", "Title is required"), 400);
        if (!TaskEnums.ValidPriorities.Contains(req.Priority))
            return ApiResult<TaskResponse>.Fail(
                new Error("invalid_request", $"Priority must be one of: {string.Join(", ", TaskEnums.ValidPriorities)}"), 400);
        if (!IsExactlyOneAssignmentTarget(req.AssignedToUserId, req.AssignedToRoleKey))
            return ApiResult<TaskResponse>.Fail(
                new Error("invalid_request", "Exactly one of assigned_to_user_id or assigned_to_role_key is required"), 400);
        if (req.AssignedToRoleKey is { } roleKey && !TaskEnums.ValidRoleKeys.Contains(roleKey))
            return ApiResult<TaskResponse>.Fail(
                new Error("invalid_request", $"assigned_to_role_key must be one of: {string.Join(", ", TaskEnums.ValidRoleKeys)}"), 400);

        var created = await repo.CreateAsync(tid, uid, req, ct);
        if (created is null)
            return ApiResult<TaskResponse>.Fail(new Error("server_error", "could not create task"), 500);
        return ApiResult<TaskResponse>.Ok(created, 201);
    }

    public async Task<ApiResult<CursorPage<TaskResponse>>> ListAllAsync(
        TaskListFilter filter, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!RoleChecks.IsTaskManager(caller))
            return ApiResult<CursorPage<TaskResponse>>.Fail(new Error("forbidden", "manager only"), 403);
        if (filter.Status is { } status && !TaskEnums.ValidStatuses.Contains(status))
            return ApiResult<CursorPage<TaskResponse>>.Fail(
                new Error("invalid_request", $"status must be one of: {string.Join(", ", TaskEnums.ValidStatuses)}"), 400);
        if (filter.AssignedToRoleKey is { } roleKey && !TaskEnums.ValidRoleKeys.Contains(roleKey))
            return ApiResult<CursorPage<TaskResponse>>.Fail(
                new Error("invalid_request", $"assigned_to_role_key must be one of: {string.Join(", ", TaskEnums.ValidRoleKeys)}"), 400);

        var (rows, nextCursor) = await repo.ListAllAsync(filter, ct);
        return ApiResult<CursorPage<TaskResponse>>.Ok(new CursorPage<TaskResponse>(rows, nextCursor));
    }

    public async Task<ApiResult<IReadOnlyList<PersonTaskSummary>>> ListPeopleSummaryAsync(
        ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!RoleChecks.IsTaskManager(caller))
            return ApiResult<IReadOnlyList<PersonTaskSummary>>.Fail(new Error("forbidden", "manager only"), 403);
        var today = SchoolClock.ToSchoolLocal(clock.UtcNow);
        return ApiResult<IReadOnlyList<PersonTaskSummary>>.Ok(await repo.ListPeopleSummaryAsync(today, ct));
    }

    public async Task<ApiResult<IReadOnlyList<RoleTaskSummary>>> ListRoleSummaryAsync(
        ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (!RoleChecks.IsTaskManager(caller))
            return ApiResult<IReadOnlyList<RoleTaskSummary>>.Fail(new Error("forbidden", "manager only"), 403);
        var today = SchoolClock.ToSchoolLocal(clock.UtcNow);
        return ApiResult<IReadOnlyList<RoleTaskSummary>>.Ok(await repo.ListRoleSummaryAsync(today, ct));
    }

    /// Pure validation rule (also covered directly by unit tests): exactly one of the two
    /// assignment targets must be present — never both, never neither.
    public static bool IsExactlyOneAssignmentTarget(Guid? assignedToUserId, string? assignedToRoleKey) =>
        (assignedToUserId is not null) != (!string.IsNullOrWhiteSpace(assignedToRoleKey));

    private async Task<IReadOnlyList<StaffTaskDto>> MineAsync(Guid uid, CancellationToken ct)
    {
        var roleKey = await repo.GetCallerRoleKeyAsync(uid, ct);
        var rows = await repo.ListForCallerAsync(uid, roleKey, ct);
        var today = SchoolClock.ToSchoolLocal(clock.UtcNow);
        return rows.Select(r => ToDto(r, today)).ToList();
    }

    private static StaffTaskDto ToDto(TaskResponse r, DateTime today) => new(
        r.Id, r.Title, r.Detail, r.Priority, r.Status == "completed",
        TaskDueLabelFormatter.Format(r.DueDate, today), r.PhotoUrl, r.Remarks);

    /// A task is completable/photo-attachable by the caller only if it's assigned to them
    /// specifically, or broadcast to everyone holding their duty role (and not yet claimed by a
    /// specific person). RLS already confines GetAsync to the caller's own tenant, so a
    /// cross-tenant id resolves to "not found" here exactly like a truly-missing id.
    private async Task<(Error? Error, int StatusCode)> AuthorizeAssignedAsync(Guid id, Guid uid, CancellationToken ct)
    {
        if (await repo.GetAsync(id, ct) is not { } task)
            return (new Error("not_found", "resource not found"), 404);

        var roleKey = await repo.GetCallerRoleKeyAsync(uid, ct);
        var isMine = task.AssignedToUserId == uid;
        var isMyRoleBroadcast = task.AssignedToUserId is null && roleKey is not null && task.AssignedToRoleKey == roleKey;
        if (!isMine && !isMyRoleBroadcast)
            return (new Error("forbidden", "not your task"), 403);

        return (null, 200);
    }
}
