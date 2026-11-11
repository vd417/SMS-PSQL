namespace Sms.Shared.Kernel.Auth;

/// Shown when a school suspends a login. The same wording is used for the CRM and the app.
public static class AccountSuspension
{
    public const string Code = "access_suspended";
    public const string Message =
        "Your account has been suspended by your school. Please contact your school. You can sign in again after the school removes the suspension.";
}
