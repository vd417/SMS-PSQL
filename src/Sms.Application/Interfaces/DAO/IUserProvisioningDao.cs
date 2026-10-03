using Sms.Application.DTOs.Users;
using Sms.Shared.Kernel.Auth;

namespace Sms.Application.Interfaces.DAO;

public interface IUserProvisioningDao
{
    Task<Guid> CreateUserAsync(Guid tenantId, string? email, string? phone, bool isPlatform, string[] roles,
        CancellationToken ct = default, string? studentId = null, bool mustSetPassword = false);
    Task<ImportResult> BulkCreateAsync(Guid tenantId, IReadOnlyList<ImportRow> rows, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolUserListRow>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> UserInTenantAsync(Guid userId, Guid tenantId, CancellationToken ct = default);

    /// Hard-removes a just-created user row and its role rows in one atomic statement. Used only to
    /// compensate a failed invite — when the ContactClaims sync rejects AFTER User_Create has
    /// committed — so a conflict never leaves an orphaned Users row behind. Not a general
    /// deactivation path; access removal for an established user is <see cref="SetStatusAsync"/>.
    Task DeleteUserAsync(Guid userId, CancellationToken ct = default);
    Task ReplaceRolesAsync(Guid userId, string[] roles, CancellationToken ct = default);
    Task<IReadOnlyList<PermissionOverrideDto>> GetPermissionsAsync(Guid userId, CancellationToken ct = default);
    Task SetPermissionsAsync(Guid userId, IReadOnlyList<PermissionOverrideDto> overrides, CancellationToken ct = default);
    Task SetStatusAsync(Guid userId, string status, CancellationToken ct = default);
}

public sealed record SchoolUserListRow(
    Guid Id,
    string? Email,
    string? Phone,
    string Status,
    DateTime CreatedAt,
    string Roles);
