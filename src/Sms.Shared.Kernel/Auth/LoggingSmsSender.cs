using Microsoft.Extensions.Logging;

namespace Sms.Shared.Kernel.Auth;

/// Development / stub SMS — logs instead of calling a real SMS gateway.
public interface ISmsSender
{
    Task SendAsync(string toPhone, string body, CancellationToken ct = default);
}

/// Stub — no real SMS gateway is wired up yet, so this is the only delivery path in every
/// environment today. Always "sends" so existing flows keep working. The message body is never
/// logged: invite and OTP texts can contain one-time codes.
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger, SmsOptions platform) : ISmsSender
{
    public Task SendAsync(string toPhone, string body, CancellationToken ct = default)
    {
        var sender = OutboundSms.SenderId ?? PlatformSender();
        logger.LogInformation("SMS queued to {Phone} from {Sender}", toPhone, sender);
        return Task.CompletedTask;
    }

    private string PlatformSender()
    {
        var id = (platform.SenderId ?? "").Trim().ToUpperInvariant();
        return id.Length == 0 ? "CATRE" : id;
    }
}
