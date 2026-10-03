namespace Sms.Shared.Kernel.Push;

public sealed class ExpoPushOptions
{
    public const string SectionName = "Expo";
    public string BaseUrl { get; set; } = "https://exp.host";
    public string? AccessToken { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxBatchSize { get; set; } = 100;
}
