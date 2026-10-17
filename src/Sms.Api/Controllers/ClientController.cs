using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Tenancy;
using Sms.Modules.Tenancy.Contracts;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize(Policy = "platform")]
public sealed class ClientController(ITenancyService tenancy) : ApiControllerBase
{
    [HttpGet("clients")]
    public async Task<IActionResult> List(string? status, string? tier, string? q, CancellationToken ct) =>
        CursorOk(await tenancy.ListClientsAsync(status, tier, q, ct));

    [HttpGet("clients/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        FromResult(await tenancy.GetClientAsync(id, ct));

    [HttpPost("clients")]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest req, CancellationToken ct) =>
        FromResult(await tenancy.CreateClientAsync(req, ct));

    [HttpPatch("clients/{id:guid}")]
    public async Task<IActionResult> UpdateProfile(Guid id, [FromBody] UpdateSchoolProfileRequest req, CancellationToken ct) =>
        FromResult(await tenancy.UpdateClientProfileAsync(id, req, ct));

    [HttpPost("clients/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetStatusRequest req, CancellationToken ct) =>
        FromResult(await tenancy.SetClientStatusAsync(id, req, ct));

    [HttpDelete("clients/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromBody] DeleteClientRequest req, CancellationToken ct) =>
        FromResult(await tenancy.DeleteClientAsync(id, req, ct));

    [HttpPost("clients/{id:guid}/change-plan")]
    public async Task<IActionResult> ChangePlan(Guid id, [FromBody] ChangePlanRequest req, CancellationToken ct) =>
        FromResult(await tenancy.ChangeClientPlanAsync(id, req, ct));

    /// <summary>Raise a school's billed seat limit (CATRE team action).</summary>
    [HttpPost("clients/{id:guid}/seats")]
    public async Task<IActionResult> IncreaseSeats(Guid id, [FromBody] IncreaseSeatsRequest req, CancellationToken ct) =>
        FromResult(await tenancy.IncreaseClientSeatsAsync(id, req, ct));
}
