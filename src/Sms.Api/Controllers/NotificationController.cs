using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

public sealed record RegisterPushDeviceRequest(string? Token, string? Platform, string? App);

[Route("v1")]
[Authorize]
public sealed class NotificationController(INotificationService notifications, IPushDeviceService pushDevices) : ApiControllerBase
{
    [HttpGet("notifications")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        FromResult(await notifications.ListAsync(ct));

    [HttpPost("notifications/read")]
    public async Task<IActionResult> MarkRead(CancellationToken ct) =>
        FromResult(await notifications.MarkReadAsync(ct));

    [HttpPost("push-devices")]
    public async Task<IActionResult> RegisterPushDevice([FromBody] RegisterPushDeviceRequest req, CancellationToken ct) =>
        FromResult(await pushDevices.RegisterAsync(req.Token, req.Platform, req.App, ct));

    [HttpDelete("push-devices")]
    public async Task<IActionResult> RemovePushDevice([FromBody] RegisterPushDeviceRequest req, CancellationToken ct) =>
        FromResult(await pushDevices.RemoveAsync(req.Token, ct));

    [HttpGet("crm-presence")]
    public async Task<IActionResult> Presence(CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await notifications.ListPresenceAsync(ct));
    }

    [HttpPost("notifications")]
    public async Task<IActionResult> Create([FromBody] CreateNotificationRequest req, CancellationToken ct)
    {
        if (!RoleChecks.IsStaff(User))
            return ForbiddenResult("staff only");
        return FromResult(await notifications.CreateAsync(req, ct));
    }
}
