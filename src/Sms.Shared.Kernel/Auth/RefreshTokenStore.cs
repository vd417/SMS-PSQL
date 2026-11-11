using Sms.Shared.Kernel.Data;

namespace Sms.Shared.Kernel.Auth;

public sealed class RefreshTokenStore(IDbConnectionFactory factory) : BaseRepository(factory), IRefreshTokenStore
{
    public Task SaveAsync(Guid userId, string tokenHash, DateTime expiresAt, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.RefreshToken_Insert",
            new { UserId = userId, TokenHash = tokenHash, ExpiresAt = expiresAt }, ct);

    public async Task<Guid?> GetActiveUserIdAsync(string tokenHash, CancellationToken ct = default)
    {
        var rows = await QueryProcAsync<Guid>("dbo.RefreshToken_GetActive", new { TokenHash = tokenHash }, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task RevokeAsync(string tokenHash, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.RefreshToken_Revoke", new { TokenHash = tokenHash }, ct);

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."RefreshTokens"
            SET "RevokedAt" = now()
            WHERE "UserId" = @userId AND "RevokedAt" IS NULL
            """,
            new { userId }, ct);
}
