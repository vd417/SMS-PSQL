using Sms.Shared.Kernel.Data;

namespace Sms.Shared.Kernel.Auth;

public sealed record UserDirectoryMatch(Guid Id, string Name, string Type, string? Email);

public interface IUserDirectoryLookup
{
    Task<IReadOnlyList<UserDirectoryMatch>> SearchByNameAsync(string name, CancellationToken ct = default);

    /// Single-id lookup, used to re-fetch a previously-resolved admin/owner/principal's CURRENT name
    /// on a conversation follow-up (Task 12) -- never trust a name carried in from prior context,
    /// always re-read it fresh at the point of use.
    Task<UserDirectoryMatch?> GetByIdAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Searches dbo.Users by name for admin/owner/principal person-lookup only -- never for
/// parent/student/teacher/staff-only accounts, which are directory data for PersonResolver's other
/// three sources. Relies on the same RLS/ITenantContext session-scoping every other repository in
/// this codebase already gets from IDbConnectionFactory -- no manual TenantId filter is written here,
/// matching convention.
/// <para>
/// Role priority when a single Users row carries more than one of the three admin-like roles (rare,
/// e.g. an owner who is also flagged principal): owner &gt; principal &gt; admin, an arbitrary but
/// defined, documented order -- never ambiguous about which Type a match reports.
/// </para>
/// </summary>
public sealed class UserDirectoryRepository(IDbConnectionFactory factory)
    : BaseRepository(factory), IUserDirectoryLookup
{
    public async Task<IReadOnlyList<UserDirectoryMatch>> SearchByNameAsync(string name, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<(Guid Id, string Name, string? Email, string Roles)>(
            @"SELECT u.""Id"", u.""Name"", u.""Email"",
                     string_agg(ur.""Role"", ',' ORDER BY ur.""Role"") AS ""Roles""
              FROM ""dbo"".""Users"" u
              JOIN ""dbo"".""UserRoles"" ur ON ur.""UserId"" = u.""Id""
              WHERE u.""Name"" IS NOT NULL
                AND u.""Name"" LIKE '%' || @name || '%'
                AND ur.""Role"" IN ('school.owner', 'school.principal', 'school.admin')
              GROUP BY u.""Id"", u.""Name"", u.""Email""",
            new { name }, ct);

        return rows.Select(r => new UserDirectoryMatch(r.Id, r.Name, TypeFor(r.Roles), r.Email)).ToList();
    }

    public async Task<UserDirectoryMatch?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<(Guid Id, string Name, string? Email, string Roles)>(
            @"SELECT u.""Id"", u.""Name"", u.""Email"",
                     string_agg(ur.""Role"", ',' ORDER BY ur.""Role"") AS ""Roles""
              FROM ""dbo"".""Users"" u
              JOIN ""dbo"".""UserRoles"" ur ON ur.""UserId"" = u.""Id""
              WHERE u.""Id"" = @id AND u.""Name"" IS NOT NULL
                AND ur.""Role"" IN ('school.owner', 'school.principal', 'school.admin')
              GROUP BY u.""Id"", u.""Name"", u.""Email""",
            new { id }, ct);
        var row = rows.FirstOrDefault();
        return row.Id == Guid.Empty ? null : new UserDirectoryMatch(row.Id, row.Name, TypeFor(row.Roles), row.Email);
    }

    private static string TypeFor(string roles) =>
        roles.Contains("school.owner") ? "owner" :
        roles.Contains("school.principal") ? "principal" : "admin";
}
