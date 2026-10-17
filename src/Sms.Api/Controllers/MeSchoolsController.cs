using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Auth;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Modules.Tenancy.Contracts;

namespace Sms.Api.Controllers;

[Route("v1/me")]
[Authorize]
public sealed class MeSchoolsController(IMeSchoolsService schools, IUserSettingsService settings) : ApiControllerBase
{
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct) =>
        FromResult(await settings.GetAsync(ct));

    [HttpPatch("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateUserAppSettingsRequest req, CancellationToken ct) =>
        FromResult(await settings.UpdateAsync(req, ct));

    [HttpGet("schools")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        CursorOk(await schools.ListAsync(ct));

    [HttpGet("schools/fee-summary")]
    public async Task<IActionResult> FeeSummary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        OkData(await schools.FeeSummaryAsync(from, to, ct));

    [HttpPost("schools")]
    public async Task<IActionResult> Create([FromBody] CreateMySchoolRequest req, CancellationToken ct) =>
        FromResult(await schools.CreateAsync(req, ct));

    [HttpPatch("schools/{tenantId:guid}")]
    public async Task<IActionResult> Update(
        Guid tenantId, [FromBody] UpdateSchoolProfileRequest req, CancellationToken ct) =>
        FromResult(await schools.UpdateAsync(tenantId, req, ct));

    [HttpPost("schools/{tenantId:guid}/seat-requests")]
    public async Task<IActionResult> RequestSeats(
        Guid tenantId, [FromBody] RaiseSeatRequest req, CancellationToken ct) =>
        FromResult(await schools.RequestSeatsAsync(tenantId, req, ct));

    [HttpGet("schools/{tenantId:guid}/seat-requests")]
    public async Task<IActionResult> ListSeatRequests(Guid tenantId, CancellationToken ct) =>
        OkData(await schools.ListSeatRequestsAsync(tenantId, ct));

    [HttpDelete("schools/{tenantId:guid}")]
    public async Task<IActionResult> Delete(Guid tenantId, [FromBody] DeleteClientRequest req, CancellationToken ct) =>
        FromResult(await schools.DeleteAsync(tenantId, req, ct));

    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct) =>
        OkData(await schools.ListPublishedPlansAsync(ct));

    [HttpPost("switch-school")]
    public async Task<IActionResult> SwitchSchool([FromBody] SwitchSchoolRequest req, CancellationToken ct) =>
        FromResult(await schools.SwitchTenantAsync(req.TenantId, ct));
}

public sealed record SwitchSchoolRequest(Guid TenantId);
