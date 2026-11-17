namespace Sms.Shared.Kernel.Auth;

/// Routes by channel: "email" stays on email. "sms" is the Catre phone OTP sender.
public sealed class ChannelOtpSender(EmailOtpSender email, IPhoneOtpSender phone) : IOtpSender
{
    public Task<string> SendAsync(string identifier, string channel, CancellationToken ct = default) =>
        channel == "email"
            ? email.SendAsync(identifier, channel, ct)
            : phone.SendAsync(identifier, channel, ct);
}
