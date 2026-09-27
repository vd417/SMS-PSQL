using System.Security.Claims;
using Sms.Application.Common;
using Sms.Application.Services.Auth;
using Sms.Application.Services.Sis;
using Sms.Modules.Academics.Data;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Comms;

public interface IPtmService
{
    Task<ApiResult<IReadOnlyList<PtmMeetingResponse>>> ListAsync(
        ClaimsPrincipal caller, string? status, string? from, string? to, Guid? teacherId, Guid? studentId,
        CancellationToken ct = default);
    Task<ApiResult<PtmMeetingResponse>> CreateAsync(
        CreatePtmRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult<PtmMeetingResponse>> UpdateAsync(
        Guid id, UpdatePtmRequest req, ClaimsPrincipal caller, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct = default);
}

public sealed class PtmService(PtmRepository repo, ISisService sis, ClassRepository classes, ITenantContext tenant)
    : IPtmService
{
    private static readonly string[] Statuses = ["pending", "confirmed"];

    public async Task<ApiResult<IReadOnlyList<PtmMeetingResponse>>> ListAsync(
        ClaimsPrincipal caller, string? status, string? from, string? to, Guid? teacherId, Guid? studentId,
        CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);

        var roles = caller.FindAll("role").Select(c => c.Value).ToList();

        if (RoleChecks.IsManagerTier(caller))
        {
            if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
                return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(
                    new Error("validation_failed", "from/to must be yyyy-MM-dd"), 422);
            return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Ok(
                await repo.ListForStaffAsync(tid, teacherId, studentId, status, fromDate, toDate, ct));
        }

        if (roles.Contains(Policies.Teacher))
        {
            if (tenant.UserId is not { } uid)
                return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(new Error("forbidden", "no teacher profile"), 403);
            var teacherOwnId = await classes.TeacherIdForUserAsync(uid, ct);
            if (teacherOwnId is null)
                return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(new Error("forbidden", "no teacher profile"), 403);
            if (!TryParseDate(from, out var fromDate) || !TryParseDate(to, out var toDate))
                return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(
                    new Error("validation_failed", "from/to must be yyyy-MM-dd"), 422);
            return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Ok(
                await repo.ListForStaffAsync(tid, teacherOwnId, null, status, fromDate, toDate, ct));
        }

        if (AppLoginRole.IsParent(roles) || AppLoginRole.IsStudent(roles))
        {
            // The caller's own roster row (student login) plus every linked child (parent login).
            var ids = new List<Guid>();
            var mine = await sis.GetMyStudentAsync(ct);
            if (mine.IsSuccess) ids.Add(mine.Data!.Id);
            var kids = await sis.ListMyChildrenAsync(ct);
            if (kids.IsSuccess) ids.AddRange(kids.Data!.Select(k => k.Id));
            if (ids.Count == 0) return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Ok([]);

            return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Ok(
                await repo.ListForStudentsAsync(tid, ids.Distinct().ToArray(), ct));
        }

        return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(new Error("forbidden", "forbidden"), 403);
    }

    public async Task<ApiResult<PtmMeetingResponse>> CreateAsync(
        CreatePtmRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        var roles = caller.FindAll("role").Select(c => c.Value).ToList();
        Guid teacherId;

        if (RoleChecks.IsManagerTier(caller))
        {
            if (req.TeacherId is not { } given)
                return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "teacher_id is required"), 422);
            teacherId = given;
        }
        else if (roles.Contains(Policies.Teacher))
        {
            if (tenant.UserId is not { } uid)
                return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no teacher profile"), 403);
            var ownId = await classes.TeacherIdForUserAsync(uid, ct);
            if (ownId is null)
                return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no teacher profile"), 403);
            teacherId = ownId.Value; // a teacher's teacher_id is always forced to their own
        }
        else
        {
            return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "forbidden"), 403);
        }

        if (req.StudentId is not { } studentId)
            return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "student_id is required"), 422);
        if (!DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", out var date))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "date must be yyyy-MM-dd"), 422);
        if (!TimeOnly.TryParseExact(req.Time, "HH:mm", out var time))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "time must be HH:mm"), 422);
        if (string.IsNullOrWhiteSpace(req.Mode) || req.Mode.Length > 120)
            return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "mode is required (max 120 chars)"), 422);
        if (req.Subject is { Length: > 120 })
            return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "subject must be at most 120 chars"), 422);

        if (!await repo.StudentExistsAsync(tid, studentId, ct))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("not_found", "resource not found"), 404);
        if (!await repo.TeacherExistsAsync(tid, teacherId, ct))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("not_found", "resource not found"), 404);

        var id = await repo.CreateAsync(tid, studentId, teacherId, req.Subject, date, time, req.Mode!, ct);
        return ApiResult<PtmMeetingResponse>.Ok((await repo.GetAsync(id, tid, ct))!, 201);
    }

    public async Task<ApiResult<PtmMeetingResponse>> UpdateAsync(
        Guid id, UpdatePtmRequest req, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        var roles = caller.FindAll("role").Select(c => c.Value).ToList();

        if (RoleChecks.IsManagerTier(caller) || roles.Contains(Policies.Teacher))
        {
            Guid? ownTeacherId = null;
            if (!RoleChecks.IsManagerTier(caller))
            {
                if (tenant.UserId is not { } uid)
                    return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no teacher profile"), 403);
                ownTeacherId = await classes.TeacherIdForUserAsync(uid, ct);
                if (ownTeacherId is null)
                    return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no teacher profile"), 403);
            }

            var meeting = await repo.GetAsync(id, tid, ct);
            if (meeting is null || (ownTeacherId is not null && meeting.TeacherId != ownTeacherId))
                return ApiResult<PtmMeetingResponse>.Fail(new Error("not_found", "resource not found"), 404);

            DateOnly? date = null;
            if (req.Date is not null)
            {
                if (!DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", out var parsedDate))
                    return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "date must be yyyy-MM-dd"), 422);
                date = parsedDate;
            }
            TimeOnly? time = null;
            if (req.Time is not null)
            {
                if (!TimeOnly.TryParseExact(req.Time, "HH:mm", out var parsedTime))
                    return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "time must be HH:mm"), 422);
                time = parsedTime;
            }
            if (req.Mode is { Length: 0 } or { Length: > 120 })
                return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "mode must be at most 120 chars"), 422);
            if (req.Subject is { Length: > 120 })
                return ApiResult<PtmMeetingResponse>.Fail(new Error("validation_failed", "subject must be at most 120 chars"), 422);

            var resetStatus = date is not null || time is not null;
            await repo.UpdateAsync(id, tid, req.Subject, date, time, req.Mode, resetStatus, ct);
            return ApiResult<PtmMeetingResponse>.Ok((await repo.GetAsync(id, tid, ct))!);
        }

        if (AppLoginRole.IsParent(roles))
        {
            if (req.Status is null || !Statuses.Contains(req.Status))
                return ApiResult<PtmMeetingResponse>.Fail(
                    new Error("invalid_status", "status must be 'pending' or 'confirmed'"), 422);

            // Unlinked and missing meetings look the same, so ids of other families' meetings don't leak.
            var meeting = await repo.GetAsync(id, tid, ct);
            if (meeting is null || !await sis.IsLinkedToCallerAsync(meeting.Child, ct))
                return ApiResult<PtmMeetingResponse>.Fail(new Error("not_found", "resource not found"), 404);

            await repo.SetStatusAsync(id, tid, req.Status, ct);
            return ApiResult<PtmMeetingResponse>.Ok((await repo.GetAsync(id, tid, ct))!);
        }

        return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "forbidden"), 403);
    }

    public async Task<ApiResult> DeleteAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);

        var roles = caller.FindAll("role").Select(c => c.Value).ToList();
        if (!RoleChecks.IsManagerTier(caller) && !roles.Contains(Policies.Teacher))
            return ApiResult.Fail(new Error("forbidden", "forbidden"), 403);

        Guid? ownTeacherId = null;
        if (!RoleChecks.IsManagerTier(caller))
        {
            if (tenant.UserId is not { } uid)
                return ApiResult.Fail(new Error("forbidden", "no teacher profile"), 403);
            ownTeacherId = await classes.TeacherIdForUserAsync(uid, ct);
            if (ownTeacherId is null)
                return ApiResult.Fail(new Error("forbidden", "no teacher profile"), 403);
        }

        var meeting = await repo.GetAsync(id, tid, ct);
        if (meeting is null || (ownTeacherId is not null && meeting.TeacherId != ownTeacherId))
            return ApiResult.Fail(new Error("not_found", "resource not found"), 404);

        await repo.DeleteAsync(id, tid, ct);
        return ApiResult.Ok(204);
    }

    private static bool TryParseDate(string? s, out DateOnly? result)
    {
        if (s is null) { result = null; return true; }
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", out var d)) { result = d; return true; }
        result = null;
        return false;
    }
}
