using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Staffing;
using Sms.Modules.Staffing.Contracts;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class LeaveController(IStaffingService staffing) : ApiControllerBase
{
    [HttpGet("leave")]
    public async Task<IActionResult> ListMine(CancellationToken ct) =>
        FromResult(await staffing.ListMyLeaveAsync(ct));

    [HttpGet("leave/balances")]
    public async Task<IActionResult> Balances(CancellationToken ct) =>
        FromResult(await staffing.GetMyLeaveBalancesAsync(ct));

    [HttpPost("leave")]
    public async Task<IActionResult> Create([FromBody] CreateLeaveRequest req, CancellationToken ct) =>
        FromResult(await staffing.CreateLeaveAsync(req, ct));

    [HttpGet("approvals")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> ListApprovals([FromQuery] string? status, CancellationToken ct)
    {
        var isManager = User.IsInRole(Policies.Principal)
            || User.IsInRole(Policies.SchoolAdmin) || User.IsInRole(Policies.SchoolOwner);
        return FromResult(await staffing.ListApprovalsAsync(status, isManager, ct));
    }

    [HttpPatch("approvals/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.TeacherApp)]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideLeaveRequest req, CancellationToken ct)
    {
        var isManager = User.IsInRole(Policies.Principal)
            || User.IsInRole(Policies.SchoolAdmin) || User.IsInRole(Policies.SchoolOwner);
        return FromResult(await staffing.DecideLeaveAsync(id, req, isManager, ct));
    }
}
