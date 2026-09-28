using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class PtmController(IPtmService ptm) : ApiControllerBase
{
    [HttpGet("ptm")]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] Guid? teacher_id, [FromQuery] Guid? student_id, [FromQuery] string? scope, CancellationToken ct) =>
        FromResult(await ptm.ListAsync(User, status, from, to, teacher_id, student_id, scope, ct));

    [HttpPost("ptm")]
    public async Task<IActionResult> Create([FromBody] CreatePtmRequest req, CancellationToken ct) =>
        FromResult(await ptm.CreateAsync(req, User, ct));

    [HttpPatch("ptm/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePtmRequest req, CancellationToken ct) =>
        FromResult(await ptm.UpdateAsync(id, req, User, ct));

    [HttpDelete("ptm/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        FromResult(await ptm.DeleteAsync(id, User, ct));
}
