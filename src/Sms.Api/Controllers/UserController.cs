using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.DTOs.Users;
using Sms.Application.Services.Users;
using Sms.Shared.Kernel.Authz;

namespace Sms.Api.Controllers;

[Route("v1")]
[Authorize]
public sealed class UserController(IUserService users) : ApiControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> List(CancellationToken ct) =>
        FromResult(await users.ListAsync(IsSchoolAdmin(), IsPrincipal(), ct));

    [HttpGet("users/parents")]
    public async Task<IActionResult> ListParents(CancellationToken ct) =>
        FromResult(await users.ListParentLoginsAsync(IsSchoolAdmin(), ct));

    [HttpPost("users")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest req, CancellationToken ct) =>
        FromResult(await users.InviteAsync(req, IsSchoolAdmin(), IsSchoolOwner(), ct));

    [HttpPost("users/import")]
    public async Task<IActionResult> Import([FromBody] ImportUsersRequest req, CancellationToken ct) =>
        FromResult(await users.ImportAsync(req, IsSchoolAdmin(), IsSchoolOwner(), ct));

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) =>
        FromResult(await users.DeactivateAsync(id, IsSchoolAdmin(), ct));

    [HttpPut("users/{id:guid}/status")]
    public async Task<IActionResult> SetActive(Guid id, [FromBody] SetUserActiveRequest req, CancellationToken ct) =>
        FromResult(await users.SetActiveAsync(id, req.Active, IsSchoolAdmin(), IsPrincipal(), ct));

    [HttpPut("users/{id:guid}/roles")]
    public async Task<IActionResult> SetRoles(Guid id, [FromBody] SetUserRolesRequest req, CancellationToken ct) =>
        FromResult(await users.SetRolesAsync(id, req, IsSchoolAdmin(), IsSchoolOwner(), ct));

    [HttpGet("users/{id:guid}/permissions")]
    public async Task<IActionResult> GetPermissions(Guid id, CancellationToken ct) =>
        FromResult(await users.GetPermissionsAsync(id, IsSchoolAdmin(), ct));

    [HttpPut("users/{id:guid}/permissions")]
    public async Task<IActionResult> SetPermissions(Guid id, [FromBody] SetUserPermissionsRequest req, CancellationToken ct) =>
        FromResult(await users.SetPermissionsAsync(id, req, IsSchoolAdmin(), ct));

    [HttpGet("roles/permissions")]
    public async Task<IActionResult> GetRoleTemplate(CancellationToken ct) =>
        FromResult(await users.GetRoleTemplateAsync(IsSchoolAdmin(), ct));

    [HttpPut("roles/permissions")]
    public async Task<IActionResult> SetRoleTemplate([FromBody] SetRoleTemplateRequest req, CancellationToken ct) =>
        FromResult(await users.SetRoleTemplateAsync(req, IsSchoolAdmin(), ct));

    private bool IsSchoolAdmin() =>
        User.FindAll("role").Any(c => c.Value is Policies.SchoolAdmin or Policies.SchoolOwner);

    private bool IsSchoolOwner() =>
        User.FindAll("role").Any(c => c.Value == Policies.SchoolOwner);

    private bool IsPrincipal() =>
        User.FindAll("role").Any(c => c.Value == Policies.Principal);
}
