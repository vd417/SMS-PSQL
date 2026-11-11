namespace Sms.Shared.Kernel.Auth;

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// Platform sender used when a school has not turned on its own sender id.
    public string SenderId { get; set; } = "CATRE";
}
