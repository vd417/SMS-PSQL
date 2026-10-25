namespace Sms.Shared.Kernel.Auth;

public interface IJwtTokenService
{
    string IssueAccess(Guid userId, Guid? tenantId, IEnumerable<string> roles, bool isPlatform, int? accessMinutesOverride = null);
    string NewRefreshToken();
}
