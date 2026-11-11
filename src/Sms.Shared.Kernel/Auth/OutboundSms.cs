namespace Sms.Shared.Kernel.Auth;

/// Carries the school sender id into the singleton SMS sender for the current async flow.
/// Null means "use the platform (Catre) sender".
public static class OutboundSms
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? SenderId =>
        string.IsNullOrWhiteSpace(Current.Value) ? null : Current.Value;

    public static IDisposable Use(string? senderId)
    {
        var prior = Current.Value;
        Current.Value = SchoolSmsRules.Resolve(true, senderId);
        return new Pop(prior);
    }

    private sealed class Pop(string? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}
