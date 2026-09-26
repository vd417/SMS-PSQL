using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Attendance;
using Sms.Modules.Attendance;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1/me/attendance")]
[Authorize]
public sealed class AttendanceController(IAttendanceService attendance) : ApiControllerBase
{
    [HttpGet("school-location")]
    public async Task<IActionResult> GetSchoolLocation(CancellationToken ct) =>
        FromResult(await attendance.GetSchoolLocationAsync(ct));

    [HttpPut("school-location")]
    [Authorize(Policy = Policies.Principal)]
    public async Task<IActionResult> UpsertSchoolLocation([FromBody] UpsertSchoolLocationRequest req, CancellationToken ct) =>
        FromResult(await attendance.UpsertSchoolLocationAsync(req, ct));

    [HttpDelete("school-location")]
    [Authorize(Policy = Policies.Principal)]
    public async Task<IActionResult> DeleteSchoolLocation(CancellationToken ct) =>
        FromResult(await attendance.DeleteSchoolLocationAsync(ct));

    [HttpPost("punch")]
    public async Task<IActionResult> Punch([FromBody] PunchRequest req, CancellationToken ct) =>
        FromResult(await attendance.PunchAsync(req, ct));

    [HttpGet("today")]
    public async Task<IActionResult> GetToday([FromQuery] string? date, [FromQuery] int? offset_minutes, CancellationToken ct) =>
        FromResult(await attendance.GetTodayAsync(date, offset_minutes, ct));

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int? limit, [FromQuery] int? offset_minutes, CancellationToken ct) =>
        FromResult(await attendance.GetHistoryAsync(limit, offset_minutes, ct));

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? month, [FromQuery] int? offset_minutes, CancellationToken ct) =>
        FromResult(await attendance.GetSummaryAsync(month, offset_minutes, ct));
}
