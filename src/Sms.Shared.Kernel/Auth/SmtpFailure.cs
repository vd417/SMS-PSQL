using MailKit.Net.Smtp;
using MailKit.Security;

namespace Sms.Shared.Kernel.Auth;

public static class SmtpFailure
{
    /// 5xx SMTP replies and authentication failures are permanent. 4xx and network errors can be retried.
    public static bool IsPermanent(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException) return true;
            if (current is SmtpCommandException cmd && (int)cmd.StatusCode >= 500) return true;
        }
        return false;
    }
}
