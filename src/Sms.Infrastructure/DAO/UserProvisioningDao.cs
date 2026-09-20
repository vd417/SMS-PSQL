using System.Text.Json;
using Sms.Application.DTOs.Users;
using Sms.Application.Interfaces.DAO;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;

namespace Sms.Infrastructure.DAO;

public sealed class UserProvisioningDao(IDbConnectionFactory factory) : BaseRepository(factory), IUserProvisioningDao
{
    public async Task<Guid> CreateUserAsync(Guid tenantId, string? email, string? phone, bool isPlatform,
        string[] roles, CancellationToken ct = default, string? studentId = null, bool mustSetPassword = false)
    {
        var id = await QuerySingleProcAsync<Guid>("dbo.User_Create",
            new
            {
                TenantId = tenantId, Email = email, Phone = phone, IsPlatform = isPlatform,
                StudentId = studentId, MustSetPassword = mustSetPassword,
            }, ct);
        foreach (var role in roles)
            await ExecuteProcAsync("dbo.UserRole_Add", new { UserId = id, Role = role }, ct);
        return id;
    }

    public async Task<ImportResult> BulkCreateAsync(Guid tenantId, IReadOnlyList<ImportRow> rows,
        CancellationToken ct = default)
    {
        var rowsJson = JsonSerializer.Serialize(rows.Select(r => new { r.Email, r.Phone, r.Role }));
        var result = await QuerySingleProcAsync<ImportResult>(
            "dbo.Users_BulkCreate", new { TenantId = tenantId, Rows = rowsJson }, ct);
        return result ?? new ImportResult(0, rows.Count);
    }

    public async Task<IReadOnlyList<SchoolUserListRow>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await QueryProcAsync<SchoolUserListRow>("dbo.Users_ListByTenant", new { TenantId = tenantId }, ct);

    public async Task<bool> UserInTenantAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            """SELECT COUNT(1) FROM "dbo"."Users" WHERE "Id" = @UserId AND "TenantId" = @TenantId""",
            new { UserId = userId, TenantId = tenantId }, ct);
        return rows.FirstOrDefault() > 0;
    }

    public Task ReplaceRolesAsync(Guid userId, string[] roles, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.UserRoles_Replace",
            new { UserId = userId, Roles = string.Join(',', roles) }, ct);

    public async Task<IReadOnlyList<PermissionOverrideDto>> GetPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryProcAsync<PermissionRow>("dbo.UserPermissions_Get", new { UserId = userId }, ct);
        return rows.Select(r => new PermissionOverrideDto(r.Module, r.Cap, r.Effect)).ToList();
    }

    public Task SetPermissionsAsync(Guid userId, IReadOnlyList<PermissionOverrideDto> overrides, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(
            overrides.Select(o => new { module = o.Module, cap = o.Cap, effect = o.Effect }));
        return ExecuteProcAsync("dbo.UserPermissions_Set", new { UserId = userId, Json = json }, ct);
    }

    public Task SetStatusAsync(Guid userId, string status, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.User_SetStatus", new { UserId = userId, Status = status }, ct);

    private sealed record PermissionRow(string Module, string Cap, string Effect);
}
