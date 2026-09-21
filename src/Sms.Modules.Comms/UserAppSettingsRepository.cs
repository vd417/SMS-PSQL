using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

public sealed record UserAppSettingsResponse(bool ChatAlerts, bool SchoolNotices, bool InAppToasts);

public sealed record UpdateUserAppSettingsRequest(bool? ChatAlerts, bool? SchoolNotices, bool? InAppToasts);

public sealed class UserAppSettingsRow
{
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }
    public bool ChatAlerts { get; init; }
    public bool SchoolNotices { get; init; }
    public bool InAppToasts { get; init; }
}

public sealed class UserAppSettingsRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public static UserAppSettingsResponse Defaults { get; } = new(true, true, true);

    public async Task<UserAppSettingsResponse> GetOrDefaultAsync(Guid userId, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<UserAppSettingsRow>(
            "SELECT \"UserId\", \"TenantId\", \"ChatAlerts\", \"SchoolNotices\", \"InAppToasts\" FROM \"dbo\".\"UserAppSettings\" WHERE \"UserId\" = @userId",
            new { userId }, ct)).FirstOrDefault();
        return row is null
            ? Defaults
            : new UserAppSettingsResponse(row.ChatAlerts, row.SchoolNotices, row.InAppToasts);
    }

    public async Task<UserAppSettingsResponse> UpsertAsync(
        Guid tenantId, Guid userId, UserAppSettingsResponse value, CancellationToken ct = default)
    {
        await ExecuteInlineAsync(@"
INSERT INTO ""dbo"".""UserAppSettings"" (""UserId"", ""TenantId"", ""ChatAlerts"", ""SchoolNotices"", ""InAppToasts"", ""UpdatedAt"")
VALUES (@userId, @tenantId, @chatAlerts, @schoolNotices, @inAppToasts, now() AT TIME ZONE 'UTC')
ON CONFLICT (""UserId"") DO UPDATE SET
    ""ChatAlerts"" = EXCLUDED.""ChatAlerts"",
    ""SchoolNotices"" = EXCLUDED.""SchoolNotices"",
    ""InAppToasts"" = EXCLUDED.""InAppToasts"",
    ""UpdatedAt"" = EXCLUDED.""UpdatedAt"";",
            new
            {
                userId,
                tenantId,
                chatAlerts = value.ChatAlerts,
                schoolNotices = value.SchoolNotices,
                inAppToasts = value.InAppToasts,
            }, ct);
        return await GetOrDefaultAsync(userId, ct);
    }
}
