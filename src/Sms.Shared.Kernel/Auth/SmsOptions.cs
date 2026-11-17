namespace Sms.Shared.Kernel.Auth;

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// Platform sender used when a school has not turned on its own sender id.
    public string SenderId { get; set; } = "CATRE";

    /// Catre-wide WhatsApp login OTP. School DLT sender settings are not used here.
    public WhatsAppOtpOptions WhatsApp { get; set; } = new();
}

public sealed class WhatsAppOtpOptions
{
    public string ApiKey { get; set; } = "";

    /// Approved Catre Technology WhatsApp number.
    public string PhoneNumberId { get; set; } = "1420447271151765";

    /// Approved template school_app_otp.
    public string MessageId { get; set; } = "41731";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(PhoneNumberId)
        && !string.IsNullOrWhiteSpace(MessageId);
}

/// Login and forgot-password codes. Matches the WhatsApp template footer.
public static class LoginOtp
{
    public const int ExpiryMinutes = 10;
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(ExpiryMinutes);

    /// A second email code for the same login waits this long.
    public const int EmailResendWaitSeconds = 60;
    public static TimeSpan EmailResendWait { get; } = TimeSpan.FromSeconds(EmailResendWaitSeconds);

    /// A second WhatsApp code for the same login waits this long.
    public const int WhatsAppResendWaitSeconds = 300;
    public static TimeSpan WhatsAppResendWait { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan ResendWaitFor(string channel) =>
        string.Equals(channel, "sms", StringComparison.OrdinalIgnoreCase)
            ? WhatsAppResendWait
            : EmailResendWait;
}
