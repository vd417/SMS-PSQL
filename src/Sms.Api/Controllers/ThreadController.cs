using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class ThreadController(IThreadService threads) : ApiControllerBase
{
    [HttpGet("threads")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        FromResult(await threads.ListAsync(ct));

    // People a staff member can start a chat with (school management accounts).
    [HttpGet("threads/contacts")]
    public async Task<IActionResult> Contacts(CancellationToken ct) =>
        FromResult(await threads.ListContactsAsync(ct));

    [HttpPost("threads")]
    public async Task<IActionResult> Create([FromBody] CreateThreadRequest req, CancellationToken ct) =>
        FromResult(await threads.CreateAsync(req, ct));

    [HttpGet("threads/{id:guid}/messages")]
    public async Task<IActionResult> ListMessages(Guid id, CancellationToken ct) =>
        FromResult(await threads.ListMessagesAsync(id, ct));

    [HttpPost("threads/{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] SendMessageRequest req, CancellationToken ct) =>
        FromResult(await threads.SendMessageAsync(id, req, ct));
}
