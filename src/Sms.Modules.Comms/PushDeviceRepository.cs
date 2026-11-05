using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Comms;

public sealed record PushTokenRow(string Token);

public sealed class PushDeviceRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task UpsertAsync(
        Guid tenantId, Guid userId, string token, string platform, string app, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."PushDevices" ("TenantId", "UserId", "Token", "Platform", "App", "UpdatedAt")
            VALUES (@tenantId, @userId, @token, @platform, @app, now())
            ON CONFLICT ("Token") DO UPDATE SET
                "TenantId" = EXCLUDED."TenantId",
                "UserId" = EXCLUDED."UserId",
                "Platform" = EXCLUDED."Platform",
                "App" = EXCLUDED."App",
                "UpdatedAt" = now()
            """,
            new { tenantId, userId, token, platform, app }, ct);

    public Task<IReadOnlyList<PushTokenRow>> ListAsync(Guid? userId, CancellationToken ct = default) =>
        QueryInlineAsync<PushTokenRow>(
            """
            SELECT "Token" FROM "dbo"."PushDevices"
            WHERE (@userId::uuid IS NULL OR "UserId" = @userId)
            """,
            new { userId }, ct);

    public Task DeleteAsync(string token, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """DELETE FROM "dbo"."PushDevices" WHERE "Token" = @token""",
            new { token }, ct);

    public Task DeleteForUserAsync(string token, Guid userId, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """DELETE FROM "dbo"."PushDevices" WHERE "Token" = @token AND "UserId" = @userId""",
            new { token, userId }, ct);
}
