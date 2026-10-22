using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Academics;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class AcademicsPublishController(IAcademicsService academics) : ApiControllerBase
{
    [HttpGet("academic-periods")]
    public async Task<IActionResult> GetPeriods(CancellationToken ct) =>
        FromResult(await academics.GetAcademicPeriodsAsync(ct));

    [HttpPut("academic-periods")]
    [Authorize(Policy = Policies.Principal)]
    public async Task<IActionResult> UpsertPeriods([FromBody] UpsertPublishSnapshotRequest req, CancellationToken ct) =>
        FromResult(await academics.UpsertAcademicPeriodsAsync(req, ct));

    [HttpGet("class-tests")]
    public async Task<IActionResult> GetClassTests(CancellationToken ct) =>
        FromResult(await academics.GetClassTestScheduleAsync(ct));

    [HttpPut("class-tests")]
    [Authorize(Policy = Policies.Principal)]
    public async Task<IActionResult> UpsertClassTests([FromBody] UpsertPublishSnapshotRequest req, CancellationToken ct) =>
        FromResult(await academics.UpsertClassTestScheduleAsync(req, ct));

    [HttpGet("class-routine/history")]
    public async Task<IActionResult> ListTimetableHistory(CancellationToken ct) =>
        FromResult(await academics.ListTimetableHistoryAsync(ct));

    [HttpGet("class-routine")]
    public async Task<IActionResult> GetTimetableSchedule(CancellationToken ct) =>
        FromResult(await academics.GetTimetableScheduleAsync(ct));

    [HttpPut("class-routine")]
    [Authorize(Policy = Policies.Principal)]
    public async Task<IActionResult> UpsertTimetableSchedule([FromBody] UpsertPublishSnapshotRequest req, CancellationToken ct) =>
        FromResult(await academics.UpsertTimetableScheduleAsync(req, ct));
}
