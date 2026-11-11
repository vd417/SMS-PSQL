using MailKit.Net.Smtp;
using MailKit.Security;

namespace Sms.Shared.Kernel.Auth;

public static class SmtpProbe
{
    public static async Task<(bool Ok, string? Error)> TryAsync(SmtpRoute route, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient { Timeout = 15_000 };
            var secure = route.Ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(route.Host, route.Port, secure, ct);
            await client.AuthenticateAsync(route.User, route.Password, ct);
            await client.DisconnectAsync(quit: true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, PublicMessage(ex));
        }
    }

    public static string PublicMessage(Exception ex)
    {
        var text = ex.Message ?? "SMTP connection failed";
        return text.Length > 500 ? text[..500] : text;
    }
}
