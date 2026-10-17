using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Sms.Application.Common;
using Sms.Application.Services.Academics;
using Sms.Application.Services.Finance;
using Sms.Application.Services.Sis;
using Sms.Application.Services.Transport;
using Sms.Modules.Sis.Contracts;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class StudentController(
    ISisService sis, IAcademicsService academics, IStudentTransportService transport, IFeeService fees,
    ITenantContext tenant, ITenantFeatureSet features, ILogger<StudentController> logger) : ApiControllerBase
{
    /// Best-effort fee backfill after a student is fully set up — never lets a Finance-side
    /// failure turn a successful student create/transport-set into an error response.
    private async Task ApplyFeesAsync(Guid studentId, CancellationToken ct)
    {
        try
        {
            await fees.ApplyExistingFeeStructureAsync([studentId], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fee backfill failed for student {StudentId}", studentId);
        }
    }
    [HttpGet("students")]
    public async Task<IActionResult> List(
        [FromQuery] string? q, [FromQuery] string? grade, [FromQuery] string? status, [FromQuery] string? fee,
        [FromQuery] int? limit, [FromQuery] string? cursor,
        CancellationToken ct) =>
        FromCursorResult(await sis.ListStudentsAsync(q, grade, status, fee, limit, cursor, ct));

    [HttpGet("students/me")]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        FromResult(await sis.GetMyStudentAsync(ct));

    [HttpGet("students/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        FromResult(await sis.GetStudentAsync(id, ct));

    /// <summary>Billed seat limit vs live enrolment for the school, with near-limit / at-limit flags
    /// so the add-student UI can warn or block.</summary>
    [HttpGet("students/seat-usage")]
    public async Task<IActionResult> SeatUsage(CancellationToken ct) =>
        FromResult(await sis.GetSeatUsageAsync(ct));

    [HttpPost("students")]
    public async Task<IActionResult> Create([FromBody] CreateStudentRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        var result = await sis.CreateStudentAsync(req, ct);

        // Transport is a separate PUT the client issues right after this call whenever the
        // school's plan even has it — SetTransport below does the fee backfill in that case,
        // once transport is actually known, so a transport-eligible student's invoice isn't
        // created transport-less and then permanently stuck that way by the per-period
        // idempotency check. Off that tier, no such follow-up call is ever coming, so this is
        // the only "student is fully created" signal there is.
        if (result.IsSuccess && result.Data is { } created && !FeatureGate.Allowed(tenant, features, FeatureCatalog.Operations))
            await ApplyFeesAsync(created.Id, ct);

        return FromResult(result);
    }

    [HttpPatch("students/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStudentRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await sis.UpdateStudentAsync(id, req, ct));
    }

    [HttpPut("students/{id:guid}/transport")]
    public async Task<IActionResult> SetTransport(Guid id, [FromBody] SetStudentTransportRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        var result = await transport.SetAsync(id, req, ct);

        // Single Add always calls this once transport is on the plan at all — opted in or out
        // — so this is the reliable "student is now fully set up" signal for that case; see
        // Create above for the off-tier case.
        if (result.IsSuccess)
            await ApplyFeesAsync(id, ct);

        return FromResult(result);
    }

    [HttpGet("students/{id:guid}/transport")]
    public async Task<IActionResult> GetTransport(Guid id, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await transport.GetAsync(id, ct));
    }

    [HttpGet("classes/{classId:guid}/students")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> ListByClass(
        Guid classId, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct) =>
        FromCursorResult(await sis.ListClassStudentsAsync(classId, limit, cursor, ct));

    [HttpGet("students/{studentId:guid}/attendance")]
    public async Task<IActionResult> ListAttendance(
        Guid studentId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        FromResult(await academics.ListAttendanceForStudentAsync(
            studentId, from ?? DateTime.UtcNow.AddDays(-90), to ?? DateTime.UtcNow, User, ct));

    [HttpGet("students/{studentId:guid}/attendance/periods")]
    public async Task<IActionResult> ListPeriodAttendance(
        Guid studentId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        FromResult(await academics.ListPeriodAttendanceForStudentAsync(
            studentId, from ?? DateTime.UtcNow.AddDays(-90), to ?? DateTime.UtcNow, User, ct));

    /// <summary>
    /// Official period-based attendance aggregate for the student (CRM / Teacher / Student / Parent).
    /// Percentage = (present + late) / marked periods × 100; null when unmarked.
    /// </summary>
    [HttpGet("students/{studentId:guid}/attendance/summary")]
    public async Task<IActionResult> GetPeriodAttendanceSummary(
        Guid studentId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        FromResult(await academics.GetPeriodAttendanceSummaryForStudentAsync(
            studentId, from ?? DateTime.UtcNow.AddDays(-365), to ?? DateTime.UtcNow, User, ct));

    [HttpGet("students/{studentId:guid}/timetable")]
    public async Task<IActionResult> ListTimetable(Guid studentId, CancellationToken ct) =>
        FromResult(await academics.ListTimetableForStudentIdAsync(studentId, User, ct));
}
