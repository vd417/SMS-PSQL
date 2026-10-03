using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

/// Expo push-notification device tokens for the parent app. Tenant-scoped (RLS by TenantId);
/// user isolation is applied in the WHERE clause. UNIQUE ("TenantId","ExpoPushToken") — upsert
/// re-homes a token to the latest user within the same tenant.
public sealed class DeviceTokenRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<int> UpsertAsync(
        Guid tenantId, Guid userId, string expoPushToken, string platform, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."ParentDevices" ("TenantId", "UserId", "ExpoPushToken", "Platform")
            VALUES (@tenantId, @userId, @token, @platform)
            ON CONFLICT ("TenantId", "ExpoPushToken")
            DO UPDATE SET "UserId" = EXCLUDED."UserId", "Platform" = EXCLUDED."Platform", "UpdatedAt" = now()
            """,
            new { tenantId, userId, token = expoPushToken, platform }, ct);

    public Task<IReadOnlyList<string>> ListTokensForUserAsync(
        Guid tenantId, Guid userId, CancellationToken ct = default) =>
        QueryInlineAsync<string>(
            """SELECT "ExpoPushToken" FROM "dbo"."ParentDevices" WHERE "TenantId" = @tenantId AND "UserId" = @userId""",
            new { tenantId, userId }, ct);
}
