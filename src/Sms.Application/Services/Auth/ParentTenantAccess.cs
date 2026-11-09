namespace Sms.Application.Services.Auth;

/// Schools a parent may open after OTP. Suspended, cancelled, and deactivated tenants are refused.
public static class ParentTenantAccess
{
    public static bool AllowsLogin(string? status)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        return s is "active" or "trial" or "past_due";
    }
}
