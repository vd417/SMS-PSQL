using System.Security.Claims;
using Sms.Application.Common;
using Sms.Application.Services.Auth;
using Sms.Application.Services.Sis;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Comms;

public interface IPtmService
{
    Task<ApiResult<IReadOnlyList<PtmMeetingResponse>>> ListAsync(CancellationToken ct = default);
    Task<ApiResult<PtmMeetingResponse>> SetStatusAsync(
        Guid id, string? status, ClaimsPrincipal caller, CancellationToken ct = default);
}

public sealed class PtmService(PtmRepository repo, ISisService sis, ITenantContext tenant) : IPtmService
{
    private static readonly string[] Statuses = ["pending", "confirmed"];

    public async Task<ApiResult<IReadOnlyList<PtmMeetingResponse>>> ListAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<PtmMeetingResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);

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

    public async Task<ApiResult<PtmMeetingResponse>> SetStatusAsync(
        Guid id, string? status, ClaimsPrincipal caller, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (status is null || !Statuses.Contains(status))
            return ApiResult<PtmMeetingResponse>.Fail(
                new Error("invalid_status", "status must be 'pending' or 'confirmed'"), 422);
        if (!AppLoginRole.IsParent(caller.FindAll("role").Select(c => c.Value)))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("forbidden", "parent only"), 403);

        // Unlinked and missing meetings look the same, so ids of other families' meetings don't leak.
        var meeting = await repo.GetAsync(id, tid, ct);
        if (meeting is null || !await sis.IsLinkedToCallerAsync(meeting.Child, ct))
            return ApiResult<PtmMeetingResponse>.Fail(new Error("not_found", "resource not found"), 404);

        await repo.SetStatusAsync(id, tid, status, ct);
        return ApiResult<PtmMeetingResponse>.Ok((await repo.GetAsync(id, tid, ct))!);
    }
}
