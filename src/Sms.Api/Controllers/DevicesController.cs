using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Devices;

namespace Sms.Api.Controllers;

/// Device push-token registration for the current app user.
[Route("v1/me")]
[Authorize]
public sealed class DevicesController(IDeviceService devices) : ApiControllerBase
{
    [HttpPost("devices")]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req, CancellationToken ct) =>
        FromResult(await devices.RegisterAsync(req, ct));
}
