using System.Text.Json;
using Sms.Application.Common;
using Sms.Application.Interfaces.DAO;
using Sms.Modules.Staffing.Contracts;
using Sms.Modules.Staffing.Data;
using Sms.Modules.Staffing.Profile;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Http;
using Sms.Shared.Kernel.Results;
using Sms.Application.Services.Realtime;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Staffing;

public interface IStaffingService
{
    Task<ApiResult<CursorPage<TeacherResponse>>> ListTeachersAsync(
        string? q, string? dept, string? status, CancellationToken ct = default);
    Task<ApiResult<TeacherResponse>> GetTeacherAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<TeacherResponse>> CreateTeacherAsync(CreateTeacherRequest req, CancellationToken ct = default);
    Task<ApiResult<TeacherResponse>> UpdateTeacherAsync(Guid id, UpdateTeacherRequest req, CancellationToken ct = default);

    Task<ApiResult<CursorPage<StaffResponse>>> ListStaffAsync(
        string? q, string? cat, CancellationToken ct = default);
    Task<ApiResult<StaffResponse>> GetStaffAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<StaffResponse>> CreateStaffAsync(CreateStaffRequest req, CancellationToken ct = default);
    Task<ApiResult<StaffResponse>> UpdateStaffAsync(Guid id, UpdateStaffRequest req, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<LeaveResponse>>> ListMyLeaveAsync(CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<LeaveBalanceResponse>>> GetMyLeaveBalancesAsync(CancellationToken ct = default);
    Task<ApiResult<LeaveResponse>> CreateLeaveAsync(CreateLeaveRequest req, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<LeaveResponse>>> ListApprovalsAsync(string? status, bool isManager, CancellationToken ct = default);
    Task<ApiResult<LeaveResponse>> DecideLeaveAsync(Guid id, DecideLeaveRequest req, CancellationToken ct = default);

    // Admin/principal document management for a specific staff member — distinct from
    // GET /v1/staff/profile, which is the self-service read for the CALLER's own documents.
    Task<ApiResult<IReadOnlyList<StaffDocumentResponse>>> ListStaffDocumentsAsync(Guid staffId, CancellationToken ct = default);
    Task<ApiResult<StaffDocumentResponse>> CreateStaffDocumentAsync(Guid staffId, CreateStaffDocumentRequest req, CancellationToken ct = default);
    Task<ApiResult<StaffDocumentResponse>> UpdateStaffDocumentAsync(Guid staffId, Guid docId, UpdateStaffDocumentRequest req, CancellationToken ct = default);
    Task<ApiResult> DeleteStaffDocumentAsync(Guid staffId, Guid docId, CancellationToken ct = default);
}

public sealed class StaffingService(
    TeacherRepository teachers,
    StaffRepository staff,
    LeaveRepository leave,
    ProfileRepository profile,
    IAuthDao users,
    ITenantContext tenant,
    ITenantFeatureSet features,
    ILiveBroadcaster live) : IStaffingService
{
    private bool StaffSupportAllowed => FeatureGate.Allowed(tenant, features, FeatureCatalog.StaffSupport);

    public async Task<ApiResult<CursorPage<TeacherResponse>>> ListTeachersAsync(
        string? q, string? dept, string? status, CancellationToken ct = default)
    {
        var rows = await teachers.ListAsync(q, dept, status, ct);
        return ApiResult<CursorPage<TeacherResponse>>.Ok(new CursorPage<TeacherResponse>(rows, null));
    }

    public async Task<ApiResult<TeacherResponse>> GetTeacherAsync(Guid id, CancellationToken ct = default)
    {
        var teacher = await teachers.GetAsync(id, ct);
        return teacher is null
            ? ApiResult<TeacherResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<TeacherResponse>.Ok(teacher);
    }

    public async Task<ApiResult<TeacherResponse>> CreateTeacherAsync(CreateTeacherRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<TeacherResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        try
        {
            var created = (await teachers.CreateAsync(tid, req, ct))!;
            return ApiResult<TeacherResponse>.Ok(created, 201);
        }
        catch (ContactConflictException ex)
        {
            return ApiResult<TeacherResponse>.Fail(ContactConflict(ex), 409);
        }
    }

    public async Task<ApiResult<TeacherResponse>> UpdateTeacherAsync(
        Guid id, UpdateTeacherRequest req, CancellationToken ct = default)
    {
        var existing = await teachers.GetAsync(id, ct);
        if (existing is null)
            return ApiResult<TeacherResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (req.SetPhoto)
        {
            if (ImageUrlValidation.Validate(req.PhotoUrl) is { } error)
                return ApiResult<TeacherResponse>.Fail(error, 422);
            var userId = await teachers.GetUserIdAsync(id, ct);
            if (userId is null)
                return ApiResult<TeacherResponse>.Fail(new Error("no_linked_user",
                    "This teacher hasn't accepted their sign-in invite yet — the photo can be set once they have."), 409);
            await users.SetPhotoAsync(userId.Value, ImageUrlValidation.Normalize(req.PhotoUrl), ct);
        }

        Guid? emailSyncedUserId = null;
        if (req.Email is not null && !string.Equals(req.Email, existing.Email, StringComparison.OrdinalIgnoreCase))
        {
            var userId = await teachers.GetUserIdAsync(id, ct);
            if (await SyncLinkedEmailAsync(userId, req.Email, ct) is { } error)
                return ApiResult<TeacherResponse>.Fail(error, 409);
            emailSyncedUserId = userId;
        }

        try
        {
            await teachers.UpdateAsync(id, req, ct);
        }
        catch (ContactConflictException ex)
        {
            // The proc rolled back the Teachers row, but SyncLinkedEmailAsync already committed
            // the new email to the linked Users row. Undo that so a rejected (409) edit never
            // leaves the login email diverged from the unchanged profile (e.g. a phone conflict,
            // or an email held only by an un-invited profile the Users pre-check can't see).
            if (emailSyncedUserId is { } uid)
                await users.SetEmailAsync(uid, existing.Email, ct);
            return ApiResult<TeacherResponse>.Fail(ContactConflict(ex), 409);
        }
        return ApiResult<TeacherResponse>.Ok((await teachers.GetAsync(id, ct))!);
    }

    public async Task<ApiResult<CursorPage<StaffResponse>>> ListStaffAsync(
        string? q, string? cat, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<CursorPage<StaffResponse>>(FeatureCatalog.StaffSupport);
        var rows = await staff.ListAsync(q, cat, ct);
        return ApiResult<CursorPage<StaffResponse>>.Ok(new CursorPage<StaffResponse>(rows, null));
    }

    public async Task<ApiResult<StaffResponse>> GetStaffAsync(Guid id, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<StaffResponse>(FeatureCatalog.StaffSupport);
        var member = await staff.GetAsync(id, ct);
        return member is null
            ? ApiResult<StaffResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<StaffResponse>.Ok(member);
    }

    public async Task<ApiResult<StaffResponse>> CreateStaffAsync(CreateStaffRequest req, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<StaffResponse>(FeatureCatalog.StaffSupport);
        if (tenant.TenantId is not { } tid)
            return ApiResult<StaffResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        try
        {
            var created = (await staff.CreateAsync(tid, req, ct))!;
            return ApiResult<StaffResponse>.Ok(created, 201);
        }
        catch (ContactConflictException ex)
        {
            return ApiResult<StaffResponse>.Fail(ContactConflict(ex), 409);
        }
    }

    public async Task<ApiResult<StaffResponse>> UpdateStaffAsync(
        Guid id, UpdateStaffRequest req, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<StaffResponse>(FeatureCatalog.StaffSupport);
        var existing = await staff.GetAsync(id, ct);
        if (existing is null)
            return ApiResult<StaffResponse>.Fail(new Error("not_found", "resource not found"), 404);

        if (req.SetPhoto)
        {
            if (ImageUrlValidation.Validate(req.PhotoUrl) is { } error)
                return ApiResult<StaffResponse>.Fail(error, 422);
            var userId = await staff.GetUserIdAsync(id, ct);
            if (userId is null)
                return ApiResult<StaffResponse>.Fail(new Error("no_linked_user",
                    "This staff member hasn't accepted their sign-in invite yet — the photo can be set once they have."), 409);
            await users.SetPhotoAsync(userId.Value, ImageUrlValidation.Normalize(req.PhotoUrl), ct);
        }

        Guid? emailSyncedUserId = null;
        if (req.Email is not null && !string.Equals(req.Email, existing.Email, StringComparison.OrdinalIgnoreCase))
        {
            var userId = await staff.GetUserIdAsync(id, ct);
            if (await SyncLinkedEmailAsync(userId, req.Email, ct) is { } error)
                return ApiResult<StaffResponse>.Fail(error, 409);
            emailSyncedUserId = userId;
        }

        try
        {
            await staff.UpdateAsync(id, req, ct);
        }
        catch (ContactConflictException ex)
        {
            // The proc rolled back the Staff row, but SyncLinkedEmailAsync already committed
            // the new email to the linked Users row. Undo that so a rejected (409) edit never
            // leaves the login email diverged from the unchanged profile.
            if (emailSyncedUserId is { } uid)
                await users.SetEmailAsync(uid, existing.Email, ct);
            return ApiResult<StaffResponse>.Fail(ContactConflict(ex), 409);
        }
        return ApiResult<StaffResponse>.Ok((await staff.GetAsync(id, ct))!);
    }

    /// <summary>Writes an Email edit through to the linked Users row (the single source
    /// of truth for login/GET /auth/me) when one exists; unlinked Teacher/Staff rows
    /// (not yet invited/accepted) have no Users row to sync. Returns a conflict Error
    /// if another account in the tenant already owns that email (Users has a unique
    /// (TenantId, Email) index), leaving both rows untouched.</summary>
    private async Task<Error?> SyncLinkedEmailAsync(Guid? userId, string? newEmail, CancellationToken ct)
    {
        if (userId is null)
            return null;
        if (tenant.TenantId is { } tid)
        {
            var conflictUser = await users.GetByEmailAndTenantAsync(newEmail!, tid, ct);
            if (conflictUser is not null && conflictUser.Id != userId.Value)
                return new Error("conflict", "A user with this email already exists in this school.");
        }
        await users.SetEmailAsync(userId.Value, newEmail, ct);
        return null;
    }

    /// Maps a ContactClaims uniqueness rejection raised by teacher_create/update or staff_create/update
    /// (via dbo.contact_claims_sync) to the existing friendly `conflict` error (surfaced as HTTP 409).
    private static Error ContactConflict(ContactConflictException ex) =>
        new("conflict", ex.IsPhone
            ? "A user with this phone number already exists in this school."
            : "A user with this email already exists in this school.");

    public async Task<ApiResult<IReadOnlyList<LeaveResponse>>> ListMyLeaveAsync(CancellationToken ct = default)
    {
        var rows = await leave.ListMineAsync(tenant.UserId, ct);
        return ApiResult<IReadOnlyList<LeaveResponse>>.Ok(rows);
    }

    // Canonical leave types this app knows how to show a balance for. A requester with no
    // entitlement row yet (added after the one-time seed migration, or in a later calendar
    // year with no admin UI to configure new entitlements) simply gets {total:0, used:0} for
    // that type rather than a missing/erroring balance.
    private static readonly string[] CanonicalLeaveTypes = ["casual", "sick", "earned"];

    public async Task<ApiResult<IReadOnlyList<LeaveBalanceResponse>>> GetMyLeaveBalancesAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<LeaveBalanceResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);

        var year = DateTime.UtcNow.Year;
        var rows = await leave.GetBalancesAsync(tid, tenant.UserId, year, ct);
        var byType = rows.ToDictionary(r => r.Type, StringComparer.OrdinalIgnoreCase);
        var result = CanonicalLeaveTypes
            .Select(t => byType.TryGetValue(t, out var existing) ? existing : new LeaveBalanceResponse(t, 0, 0))
            .ToList();
        return ApiResult<IReadOnlyList<LeaveBalanceResponse>>.Ok(result);
    }

    public async Task<ApiResult<LeaveResponse>> CreateLeaveAsync(CreateLeaveRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<LeaveResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        string? attachmentUrlsJson = null;
        if (req.AttachmentUrls is { Count: > 0 })
        {
            if (req.AttachmentUrls.Count > 5)
                return ApiResult<LeaveResponse>.Fail(new Error("invalid_request", "max 5 attachment URLs allowed"), 400);

            var normalized = new List<string>();
            foreach (var url in req.AttachmentUrls)
            {
                if (ImageUrlValidation.Validate(url) is { } error)
                    return ApiResult<LeaveResponse>.Fail(error, 400);
                var n = ImageUrlValidation.Normalize(url);
                if (n is not null)
                    normalized.Add(n);
            }

            if (normalized.Count > 0)
                attachmentUrlsJson = JsonSerializer.Serialize(normalized);
        }

        var created = (await leave.CreateAsync(tid, tenant.UserId, req, attachmentUrlsJson, ct))!;
        await live.PublishAsync(tid, LiveEventTypes.Leave, ct: ct);
        return ApiResult<LeaveResponse>.Ok(created, 201);
    }

    public async Task<ApiResult<IReadOnlyList<LeaveResponse>>> ListApprovalsAsync(
        string? status, bool isManager, CancellationToken ct = default)
    {
        if (isManager)
            return ApiResult<IReadOnlyList<LeaveResponse>>.Ok(await leave.ListByStatusAsync(status ?? "pending", null, ct));
        if (tenant is not { UserId: { } uid, TenantId: { } tid })
            return ApiResult<IReadOnlyList<LeaveResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);
        var childIds = await leave.StudentIdsForClassTeacherAsync(uid, tid, ct);
        var rows = await leave.ListByStatusAsync(status ?? "pending", childIds.ToArray(), ct);
        return ApiResult<IReadOnlyList<LeaveResponse>>.Ok(rows);
    }

    public async Task<ApiResult<LeaveResponse>> DecideLeaveAsync(
        Guid id, DecideLeaveRequest req, CancellationToken ct = default)
    {
        if (await leave.GetAsync(id, ct) is not { } existing)
            return ApiResult<LeaveResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (existing.RequesterId is { } reqId && reqId == tenant.UserId)
            return ApiResult<LeaveResponse>.Fail(
                new Error("forbidden", "You cannot decide your own leave request."), 403);
        var decided = (await leave.DecideAsync(id, req.Status, tenant.UserId, req.DecidedNote, ct))!;
        if (tenant.TenantId is { } tid)
            await live.PublishAsync(tid, LiveEventTypes.Leave, ct: ct);
        return ApiResult<LeaveResponse>.Ok(decided);
    }

    public async Task<ApiResult<IReadOnlyList<StaffDocumentResponse>>> ListStaffDocumentsAsync(
        Guid staffId, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<IReadOnlyList<StaffDocumentResponse>>(FeatureCatalog.StaffSupport);
        if (await staff.GetAsync(staffId, ct) is null)
            return ApiResult<IReadOnlyList<StaffDocumentResponse>>.Fail(new Error("not_found", "resource not found"), 404);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<StaffDocumentResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);
        var docs = await profile.ListForStaffAsync(tid, staffId, ct);
        return ApiResult<IReadOnlyList<StaffDocumentResponse>>.Ok(docs);
    }

    public async Task<ApiResult<StaffDocumentResponse>> CreateStaffDocumentAsync(
        Guid staffId, CreateStaffDocumentRequest req, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<StaffDocumentResponse>(FeatureCatalog.StaffSupport);
        if (await staff.GetAsync(staffId, ct) is null)
            return ApiResult<StaffDocumentResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.Value))
            return ApiResult<StaffDocumentResponse>.Fail(new Error("invalid_request", "label and value are required"), 422);
        if (tenant.TenantId is not { } tid)
            return ApiResult<StaffDocumentResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var created = (await profile.CreateAsync(tid, staffId, req, ct))!;
        return ApiResult<StaffDocumentResponse>.Ok(created, 201);
    }

    public async Task<ApiResult<StaffDocumentResponse>> UpdateStaffDocumentAsync(
        Guid staffId, Guid docId, UpdateStaffDocumentRequest req, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked<StaffDocumentResponse>(FeatureCatalog.StaffSupport);
        if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.Value))
            return ApiResult<StaffDocumentResponse>.Fail(new Error("invalid_request", "label and value are required"), 422);
        if (tenant.TenantId is not { } tid)
            return ApiResult<StaffDocumentResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        // Guarded by StaffId + TenantId inside the proc itself, not pre-checked here — a
        // cross-tenant or cross-staff docId simply matches zero rows and comes back null,
        // same as TeamRepository.DeleteDocumentAsync's guarded-WHERE-clause approach.
        var updated = await profile.UpdateAsync(tid, staffId, docId, req, ct);
        return updated is null
            ? ApiResult<StaffDocumentResponse>.Fail(new Error("not_found", "resource not found"), 404)
            : ApiResult<StaffDocumentResponse>.Ok(updated);
    }

    public async Task<ApiResult> DeleteStaffDocumentAsync(Guid staffId, Guid docId, CancellationToken ct = default)
    {
        if (!StaffSupportAllowed)
            return FeatureGate.Locked(FeatureCatalog.StaffSupport);
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        if (!await profile.DeleteAsync(tid, staffId, docId, ct))
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);
        return ApiResult.NoContent();
    }
}
