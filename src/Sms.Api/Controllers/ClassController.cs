using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Academics;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class ClassController(IAcademicsService academics) : ApiControllerBase
{
    [HttpGet("classes")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        FromResult(await academics.ListClassesAsync(User, ct));

    [HttpGet("classes/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        FromResult(await academics.GetClassAsync(id, ct));

    [HttpPost("classes")]
    public async Task<IActionResult> Create([FromBody] CreateClassRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await academics.CreateClassAsync(req, ct));
    }

    [HttpPatch("classes/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateClassRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await academics.UpdateClassAsync(id, req, ct));
    }

    [HttpGet("classes/period-overrides")]
    public async Task<IActionResult> ListPeriodOverrides(CancellationToken ct) =>
        FromResult(await academics.ListClassPeriodOverridesAsync(ct));

    [HttpPut("classes/{id:guid}/period-override")]
    public async Task<IActionResult> SetPeriodOverride(
        Guid id, [FromBody] SetClassPeriodOverrideRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await academics.SetClassPeriodOverrideAsync(id, req?.PeriodCount, ct));
    }

    [HttpGet("classes/{id:guid}/subjects")]
    public async Task<IActionResult> ListSubjects(Guid id, CancellationToken ct) =>
        FromResult(await academics.ListClassSubjectsAsync(id, ct));

    [HttpPut("classes/{id:guid}/subjects")]
    public async Task<IActionResult> ReplaceSubjects(
        Guid id, [FromBody] ReplaceClassSubjectsRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        if (req is null || req.Subjects is null)
            return BadRequestResult("subjects is required");
        return FromResult(await academics.ReplaceClassSubjectsAsync(id, req.Subjects, ct));
    }

    [HttpGet("classes/{classId:guid}/attendance/roll-call")]
    public async Task<IActionResult> GetAttendanceRollCall(
        Guid classId, [FromQuery] DateTime date, CancellationToken ct) =>
        FromResult(await academics.GetAttendanceRollCallAsync(classId, date, User, ct));

    [HttpGet("classes/{classId:guid}/attendance")]
    public async Task<IActionResult> ListAttendance(
        Guid classId,
        [FromQuery] DateTime? date,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        if (from is { } f && to is { } t)
            return FromResult(await academics.ListAttendanceRangeAsync(classId, f, t, ct));
        return FromResult(await academics.ListAttendanceAsync(classId, date ?? DateTime.UtcNow.Date, ct));
    }

    [HttpPost("classes/{classId:guid}/attendance")]
    public async Task<IActionResult> BulkUpsertAttendance(
        Guid classId, [FromBody] BulkAttendanceRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await academics.BulkUpsertAttendanceAsync(classId, req, User, ct));
    }

    [HttpGet("classes/{classId:guid}/timetable/day")]
    public async Task<IActionResult> ListClassDayTimetable(
        Guid classId, [FromQuery] DateTime date, CancellationToken ct) =>
        FromResult(await academics.ListClassDayTimetableAsync(classId, date, User, ct));

    [HttpGet("classes/{classId:guid}/attendance/periods")]
    public async Task<IActionResult> ListPeriodAttendance(
        Guid classId,
        [FromQuery] DateTime date,
        [FromQuery] int period,
        [FromQuery] string subject,
        CancellationToken ct) =>
        FromResult(await academics.ListPeriodAttendanceAsync(classId, date, period, subject ?? "", ct));

    [HttpPost("classes/{classId:guid}/attendance/periods")]
    public async Task<IActionResult> BulkUpsertPeriodAttendance(
        Guid classId, [FromBody] BulkPeriodAttendanceRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await academics.BulkUpsertPeriodAttendanceAsync(classId, req, User, ct));
    }

    /// <summary>
    /// Official period-based attendance aggregate for a class over a date range.
    /// </summary>
    [HttpGet("classes/{classId:guid}/attendance/summary")]
    public async Task<IActionResult> GetPeriodAttendanceSummary(
        Guid classId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        FromResult(await academics.GetPeriodAttendanceSummaryForClassAsync(
            classId, from ?? DateTime.UtcNow.AddDays(-30), to ?? DateTime.UtcNow, ct));
}
