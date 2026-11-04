namespace Sms.Shared.Kernel.Push;

public interface IExpoPushSender
{
    /// Best-effort: batches tokens and never throws for provider failures.
    Task SendAsync(IReadOnlyList<string> expoPushTokens, string title, string body,
        IReadOnlyDictionary<string, object?>? data = null, CancellationToken ct = default);
}
