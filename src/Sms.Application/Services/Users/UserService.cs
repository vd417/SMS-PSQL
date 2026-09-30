using Sms.Application.Common;
using Sms.Application.DTOs.Users;
using Sms.Application.Interfaces.DAO;
using Sms.Application.Services.Auth;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Users;

public interface IUserService
{
    /// <param name="canAssignOwner">True when inviter is school.owner (may assign co-owner).</param>
    Task<ApiResult<object>> InviteAsync(InviteUserRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default);
    Task<ApiResult<ImportResponse>> ImportAsync(ImportUsersRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<SchoolUserResponse>>> ListAsync(bool isSchoolAdmin, bool isPrincipal, CancellationToken ct = default);
    /// <summary>Removes a person's access to the CURRENT school only — other tenants they
    /// belong to (separate Users rows) are unaffected.</summary>
    Task<ApiResult> DeactivateAsync(Guid userId, bool isSchoolAdmin, CancellationToken ct = default);
    /// <summary>Reversible pause/resume — flips between "active" and "inactive" for an
    /// already-accepted member of the current school. Admin/Owner may target any
    /// non-owner row; a Principal (not Admin/Owner) may only target a teacher or
    /// staff account.</summary>
    Task<ApiResult<SchoolUserResponse>> SetActiveAsync(Guid userId, bool active, bool isSchoolAdmin, bool isPrincipal, CancellationToken ct = default);
    Task<ApiResult<SchoolUserResponse>> SetRolesAsync(Guid userId, SetUserRolesRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PermissionOverrideDto>>> GetPermissionsAsync(Guid userId, bool isSchoolAdmin, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<PermissionOverrideDto>>> SetPermissionsAsync(Guid userId, SetUserPermissionsRequest req, bool isSchoolAdmin, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>> GetRoleTemplateAsync(bool isSchoolAdmin, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>> SetRoleTemplateAsync(SetRoleTemplateRequest req, bool isSchoolAdmin, CancellationToken ct = default);
}

public sealed class UserService(
    IUserProvisioningDao dao,
    ITenantContext tenant,
    IAuthService auth,
    ClientRepository clients,
    IInvitationDao invitations,
    IRoleTemplateDao roleTemplates,
    AuditRepository audit,
    IContactValidator contacts) : IUserService
{
    private static readonly HashSet<string> AssignableRoleTemplateRoles = new(
        ["admin", "principal", "vice_principal", "teacher", "staff"], StringComparer.OrdinalIgnoreCase);

    /// School base roles: admin, principal, teacher (+ staff/parent). Owner only via school.owner inviter.
    private static readonly HashSet<string> BaseAssignableRoles = new(
        Policies.All.Where(r => r is not (Policies.PlatformOnly or Policies.SchoolOwner)),
        StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> AssignableFor(bool canAssignOwner)
    {
        if (!canAssignOwner) return BaseAssignableRoles;
        var set = new HashSet<string>(BaseAssignableRoles, StringComparer.OrdinalIgnoreCase) { Policies.SchoolOwner };
        return set;
    }

    public async Task<ApiResult<object>> InviteAsync(
        InviteUserRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<object>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<object>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (req.Email is null && req.Phone is null)
            return ApiResult<object>.Fail(new Error("invalid_request", "email or phone required"), 422);

        var allowed = AssignableFor(canAssignOwner);
        if (req.Roles.Length == 0 || req.Roles.Any(r => !allowed.Contains(r)))
            return ApiResult<object>.Fail(
                new Error("invalid_request",
                    canAssignOwner
                        ? "invalid role(s); use school.owner, school.admin, school.principal, school.teacher, staff, or student.parent"
                        : "invalid role(s); school.owner can only be assigned by a school owner"),
                422);

        // Users.(TenantId, Email) has no unique constraint — without this check, a retried
        // or double-submitted invite silently creates another row for the same person in
        // the same school (they'd show up N times in the Team list).
        var existingInTenant = await dao.ListByTenantAsync(tid, ct);
        if (req.Email is not null && existingInTenant.Any(u => string.Equals(u.Email, req.Email, StringComparison.OrdinalIgnoreCase)))
            return ApiResult<object>.Fail(new Error("conflict", "A user with this email already exists in this school. Resend the invite from the Invitations tab instead."), 409);
        if (req.Phone is not null && existingInTenant.Any(u => string.Equals(u.Phone, req.Phone, StringComparison.OrdinalIgnoreCase)))
            return ApiResult<object>.Fail(new Error("conflict", "A user with this phone number already exists in this school. Resend the invite from the Invitations tab instead."), 409);

        var id = await dao.CreateUserAsync(tid, req.Email, req.Phone, false, req.Roles, ct);

        // Authoritative contact-uniqueness via the ContactClaims ledger. The pre-check above
        // rejects the common same-tenant duplicate before insert; this also catches a contact
        // already claimed by a different owner/person (and concurrent-invite races) and records
        // this new user's claim. PersonId is null at create time (adopted later by backfill).
        var claim = await contacts.SyncAsync(tid, "user", id.ToString(), personId: null, req.Email, req.Phone, ct);
        if (!claim.IsValid)
            return ApiResult<object>.Fail(
                new Error("conflict", claim.ConflictKind == ContactConflictKind.Phone
                    ? "A user with this phone number already exists in this school. Resend the invite from the Invitations tab instead."
                    : "A user with this email already exists in this school. Resend the invite from the Invitations tab instead."),
                409);

        await dao.SetStatusAsync(id, "pending", ct);

        var roleLabel = RoleLabel(req.Roles.FirstOrDefault());
        await invitations.CreateAsync(tid, id, req.Email, req.Phone, roleLabel ?? "Member",
            tenant.UserId, DateTime.UtcNow.AddHours(24), ct);

        /* Welcome onboard email/SMS: school name(s) + password-setup OTP or magic link.
           SendWelcome=false when this call is one of several in a multi-school invite
           batch for the same person — only the batch's last call actually sends. */
        var inviteId = req.Channel?.Trim().ToLowerInvariant() switch
        {
            "phone" => req.Phone ?? req.Email,
            "email" => req.Email ?? req.Phone,
            _ => req.Email ?? req.Phone,
        };
        if (req.SendWelcome && !string.IsNullOrWhiteSpace(inviteId))
        {
            string schoolName;
            if (req.SchoolNames is { Length: > 0 })
                schoolName = string.Join(", ", req.SchoolNames);
            else
                schoolName = (await clients.GetAsync(tid, ct))?.Name ?? "your school";
            try { await auth.SendInviteSetupAsync(inviteId!, schoolName, roleLabel, TimeSpan.FromHours(24), req.Method, req.Message, ct); }
            catch { /* user row exists; invite email is best-effort */ }
        }
        return ApiResult<object>.Ok(new { id, invited = true }, 201);
    }

    private static string? RoleLabel(string? role) => role?.ToLowerInvariant() switch
    {
        Policies.SchoolOwner => "Owner",
        Policies.SchoolAdmin => "Admin",
        Policies.Principal => "Principal",
        "school.vice_principal" or "school.vice-principal" => "Vice-Principal",
        Policies.Teacher => "Teacher",
        Policies.Staff => "Staff",
        _ => role,
    };

    public async Task<ApiResult<ImportResponse>> ImportAsync(
        ImportUsersRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<ImportResponse>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<ImportResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        var allowed = AssignableFor(canAssignOwner);
        var valid = new List<ImportRow>();
        var errors = new List<ImportError>();
        for (var i = 0; i < req.Rows.Length; i++)
        {
            var r = req.Rows[i];
            if (r.Email is null && r.Phone is null)
                errors.Add(new ImportError(i, "email or phone required"));
            else if (r.Role is not null && !allowed.Contains(r.Role))
                errors.Add(new ImportError(i, $"invalid role '{r.Role}'"));
            else
                valid.Add(new ImportRow(r.Email, r.Phone, r.Role));
        }

        var result = await dao.BulkCreateAsync(tid, valid, ct);
        return ApiResult<ImportResponse>.Ok(new ImportResponse(result.Created, result.Skipped, errors));
    }

    public async Task<ApiResult<IReadOnlyList<SchoolUserResponse>>> ListAsync(bool isSchoolAdmin, bool isPrincipal, CancellationToken ct = default)
    {
        if (!isSchoolAdmin && !isPrincipal)
            return ApiResult<IReadOnlyList<SchoolUserResponse>>.Fail(new Error("forbidden", "school admin or principal only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<SchoolUserResponse>>.Fail(new Error("forbidden", "no tenant context"), 403);

        var rows = await dao.ListByTenantAsync(tid, ct);
        // A Principal (not Admin/Owner) may only see teacher/staff accounts here —
        // never Admin/Owner/other-Principal accounts.
        if (!isSchoolAdmin)
            rows = rows.Where(r => IsTeacherOrStaffRow(r) && !IsElevatedRow(r)).ToList();
        var list = rows.Select(MapUser).ToList();
        return ApiResult<IReadOnlyList<SchoolUserResponse>>.Ok(list);
    }

    /// Removes a person's access to THIS school only — their Users row for any other
    /// tenant they belong to is untouched (each school membership is its own row).
    public async Task<ApiResult> DeactivateAsync(Guid userId, bool isSchoolAdmin, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        var row = (await dao.ListByTenantAsync(tid, ct)).FirstOrDefault(u => u.Id == userId);
        if (row is null)
            return ApiResult.Fail(new Error("not_found", "user not found in this school"), 404);
        if (userId == tenant.UserId)
            return ApiResult.Fail(new Error("conflict", "You can't remove your own access."), 409);
        if (IsOwnerRow(row))
            return ApiResult.Fail(new Error("conflict", "An owner's access can't be removed here."), 409);

        await dao.SetStatusAsync(userId, "removed", ct);
        await audit.InsertAsync(tenant.UserId, null, null, "user.access_removed", userId.ToString(), "identity", tid, ct);
        return ApiResult.NoContent();
    }

    private static bool IsOwnerRow(SchoolUserListRow row) =>
        row.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(Policies.SchoolOwner, StringComparer.OrdinalIgnoreCase);

    private static bool IsTeacherOrStaffRow(SchoolUserListRow row) =>
        row.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => string.Equals(r, Policies.Teacher, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(r, Policies.Staff, StringComparison.OrdinalIgnoreCase));

    private static bool IsElevatedRow(SchoolUserListRow row) =>
        row.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(r => string.Equals(r, Policies.SchoolOwner, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(r, Policies.SchoolAdmin, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(r, Policies.Principal, StringComparison.OrdinalIgnoreCase));

    /// Reversible pause/resume of a person's access to THIS school — unlike
    /// DeactivateAsync ("removed"), this can be flipped back with the same call,
    /// no re-invite needed. Only valid for someone who has already accepted
    /// (status active/inactive) — pending invites use Resend, removed users need
    /// a fresh invite.
    public async Task<ApiResult<SchoolUserResponse>> SetActiveAsync(
        Guid userId, bool active, bool isSchoolAdmin, bool isPrincipal, CancellationToken ct = default)
    {
        if (!isSchoolAdmin && !isPrincipal)
            return ApiResult<SchoolUserResponse>.Fail(new Error("forbidden", "school admin or principal only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<SchoolUserResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var rows = await dao.ListByTenantAsync(tid, ct);
        var row = rows.FirstOrDefault(u => u.Id == userId);
        if (row is null)
            return ApiResult<SchoolUserResponse>.Fail(new Error("not_found", "user not found in this school"), 404);
        if (row.Status is not ("active" or "inactive"))
            return ApiResult<SchoolUserResponse>.Fail(
                new Error("invalid_request", "Only an already-accepted member can be activated or deactivated — resend the invite or remove and re-invite instead."), 422);
        if (userId == tenant.UserId)
            return ApiResult<SchoolUserResponse>.Fail(new Error("conflict", "You can't change your own access."), 409);
        if (IsOwnerRow(row))
            return ApiResult<SchoolUserResponse>.Fail(new Error("conflict", "An owner's access can't be paused here."), 409);
        // A Principal (not Admin/Owner) may only suspend/unsuspend a teacher or staff account.
        if (!isSchoolAdmin && (!IsTeacherOrStaffRow(row) || IsElevatedRow(row)))
            return ApiResult<SchoolUserResponse>.Fail(
                new Error("forbidden", "Principals can only suspend teacher or staff accounts."), 403);

        var status = active ? "active" : "inactive";
        await dao.SetStatusAsync(userId, status, ct);
        await audit.InsertAsync(tenant.UserId, null, null, "user.status_changed", $"{userId}: {status}", "identity", tid, ct);
        var updated = (await dao.ListByTenantAsync(tid, ct)).FirstOrDefault(u => u.Id == userId);
        return updated is null
            ? ApiResult<SchoolUserResponse>.Fail(new Error("not_found", "user not found"), 404)
            : ApiResult<SchoolUserResponse>.Ok(MapUser(updated));
    }

    public async Task<ApiResult<SchoolUserResponse>> SetRolesAsync(
        Guid userId, SetUserRolesRequest req, bool isSchoolAdmin, bool canAssignOwner, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<SchoolUserResponse>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<SchoolUserResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        var current = (await dao.ListByTenantAsync(tid, ct)).FirstOrDefault(u => u.Id == userId);
        if (current is null)
            return ApiResult<SchoolUserResponse>.Fail(new Error("not_found", "user not found in this school"), 404);
        // An owner's role is never changeable through this endpoint — even by another
        // owner — so nobody can be silently demoted via the generic role dropdown.
        if (IsOwnerRow(current))
            return ApiResult<SchoolUserResponse>.Fail(new Error("conflict", "An owner's role can't be changed here."), 409);

        var allowed = AssignableFor(canAssignOwner);
        if (req.Roles.Length == 0 || req.Roles.Any(r => !allowed.Contains(r)))
            return ApiResult<SchoolUserResponse>.Fail(new Error("invalid_request", "invalid role(s) for this actor"), 422);

        await dao.ReplaceRolesAsync(userId, req.Roles, ct);
        await audit.InsertAsync(tenant.UserId, null, null, "user.role_changed",
            $"{userId}: {string.Join(",", req.Roles)}", "identity", tid, ct);
        var row = (await dao.ListByTenantAsync(tid, ct)).FirstOrDefault(u => u.Id == userId);
        return row is null
            ? ApiResult<SchoolUserResponse>.Fail(new Error("not_found", "user not found"), 404)
            : ApiResult<SchoolUserResponse>.Ok(MapUser(row));
    }

    public async Task<ApiResult<IReadOnlyList<PermissionOverrideDto>>> GetPermissionsAsync(
        Guid userId, bool isSchoolAdmin, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (!await dao.UserInTenantAsync(userId, tid, ct))
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("not_found", "user not found in this school"), 404);

        var rows = await dao.GetPermissionsAsync(userId, ct);
        return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Ok(rows);
    }

    public async Task<ApiResult<IReadOnlyList<PermissionOverrideDto>>> SetPermissionsAsync(
        Guid userId, SetUserPermissionsRequest req, bool isSchoolAdmin, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("forbidden", "no tenant context"), 403);
        if (!await dao.UserInTenantAsync(userId, tid, ct))
            return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Fail(new Error("not_found", "user not found in this school"), 404);

        var cleaned = (req.Overrides ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Module)
                        && o.Cap is "V" or "E" or "A"
                        && o.Effect is "grant" or "revoke")
            .Select(o => new PermissionOverrideDto(o.Module.Trim().ToLowerInvariant(), o.Cap, o.Effect))
            .ToList();

        await dao.SetPermissionsAsync(userId, cleaned, ct);
        await audit.InsertAsync(tenant.UserId, null, null, "user.permissions_changed",
            userId.ToString(), "identity", tid, ct);
        return ApiResult<IReadOnlyList<PermissionOverrideDto>>.Ok(await dao.GetPermissionsAsync(userId, ct));
    }

    public async Task<ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>> GetRoleTemplateAsync(
        bool isSchoolAdmin, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Fail(new Error("forbidden", "no tenant context"), 403);

        return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Ok(await roleTemplates.GetAsync(tid, ct));
    }

    public async Task<ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>> SetRoleTemplateAsync(
        SetRoleTemplateRequest req, bool isSchoolAdmin, CancellationToken ct = default)
    {
        if (!isSchoolAdmin)
            return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Fail(new Error("forbidden", "school admin only"), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Fail(new Error("forbidden", "no tenant context"), 403);

        var cleaned = (req.Overrides ?? [])
            .Where(o => AssignableRoleTemplateRoles.Contains(o.Role)
                        && !string.IsNullOrWhiteSpace(o.Module)
                        && o.Cap is "V" or "E" or "A"
                        && o.Effect is "grant" or "revoke")
            .Select(o => new RoleTemplateOverrideDto(
                o.Role.Trim().ToLowerInvariant(), o.Module.Trim().ToLowerInvariant(), o.Cap, o.Effect))
            .ToList();

        await roleTemplates.SetAsync(tid, cleaned, ct);
        await audit.InsertAsync(tenant.UserId, null, null, "role_template.updated",
            $"{cleaned.Count} override(s)", "identity", tid, ct);

        return ApiResult<IReadOnlyList<RoleTemplateOverrideDto>>.Ok(await roleTemplates.GetAsync(tid, ct));
    }

    private static SchoolUserResponse MapUser(SchoolUserListRow r) =>
        new(r.Id, r.Email, r.Phone, r.Status, r.CreatedAt,
            string.IsNullOrWhiteSpace(r.Roles)
                ? []
                : r.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
