using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize(Policy = Policies.StudentOrParent)]
public sealed class PtmController(IPtmService ptm) : ApiControllerBase
{
    [HttpGet("ptm")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        FromResult(await ptm.ListAsync(ct));

    [HttpPatch("ptm/{id:guid}")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetPtmStatusRequest req, CancellationToken ct) =>
        FromResult(await ptm.SetStatusAsync(id, req.Status, User, ct));
}
