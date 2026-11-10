using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Reporting;
using Sms.Modules.Reporting.Contracts;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class SchoolReportController(ISchoolReportService reports) : ApiControllerBase
{
    [HttpGet("school-reports/catalog")]
    public IActionResult Catalog()
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return OkData(reports.Catalog());
    }

    [HttpGet("school-reports/options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.OptionsAsync(ct));
    }

    [HttpPost("school-reports/run")]
    public async Task<IActionResult> Run([FromBody] SchoolReportQuery query, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.RunAsync(query, ct));
    }

    [HttpPost("school-reports/export")]
    public async Task<IActionResult> Export([FromBody] SchoolReportQuery query, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        var result = await reports.ExportAsync(query, query.Format, ct);
        if (result.Error is { } error) return StatusCode(result.StatusCode, Sms.Shared.Kernel.Http.ErrorEnvelope.From(error));
        var file = result.Data!;
        return File(file.Bytes, file.ContentType, file.FileName);
    }

    [HttpPost("school-reports/email")]
    public async Task<IActionResult> Email([FromBody] SchoolReportEmailRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.EmailAsync(req, ct));
    }

    [HttpGet("school-reports/schedules")]
    public async Task<IActionResult> Schedules([FromQuery] string? report_key, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.ListSchedulesAsync(report_key, ct));
    }

    [HttpPost("school-reports/schedules")]
    public async Task<IActionResult> SaveSchedule([FromBody] SaveReportScheduleRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.SaveScheduleAsync(req, ct));
    }

    [HttpPost("school-reports/schedules/{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.SetStatusAsync(id, false, ct));
    }

    [HttpPost("school-reports/schedules/{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.SetStatusAsync(id, true, ct));
    }

    [HttpPost("school-reports/schedules/{id:guid}/run")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.RunNowAsync(id, ct));
    }

    [HttpGet("school-reports/schedules/{id:guid}/runs")]
    public async Task<IActionResult> Runs(Guid id, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User)) return ForbiddenResult("staff only");
        return FromResult(await reports.RunsAsync(id, ct));
    }
}
