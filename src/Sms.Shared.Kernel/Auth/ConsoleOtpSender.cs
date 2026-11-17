using System.Security.Cryptography;

namespace Sms.Shared.Kernel.Auth;

/// SMS OTP stub — no real SMS provider is wired up yet (Track C), so this is the only channel
/// for phone-based OTP delivery in every environment today. Matches EmailOtpSender's convention:
/// always generates and returns the code (callers persist its hash and complete the flow
/// regardless of environment) but only PRINTS the plaintext code to stdout when isDevelopment —
/// printing it outside Development would leak a live credential into production logs.
public sealed class ConsoleOtpSender(bool isDevelopment) : IPhoneOtpSender
{
    public Task<string> SendAsync(string identifier, string channel, CancellationToken ct = default)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        if (isDevelopment)
            Console.WriteLine($"[DEV OTP/{channel}] {identifier} -> {code} (development only)");
        return Task.FromResult(code);
    }
}
