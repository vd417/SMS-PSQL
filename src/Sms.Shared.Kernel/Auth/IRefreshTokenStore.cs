namespace Sms.Shared.Kernel.Auth;

public interface IRefreshTokenStore
{
    Task SaveAsync(Guid userId, string tokenHash, DateTime expiresAt, CancellationToken ct = default);
    Task<Guid?> GetActiveUserIdAsync(string tokenHash, CancellationToken ct = default);
    Task RevokeAsync(string tokenHash, CancellationToken ct = default);
    /// Drops every live session for this user (CRM and app) so the next call cannot refresh.
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}
