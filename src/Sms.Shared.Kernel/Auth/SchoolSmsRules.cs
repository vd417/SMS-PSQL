namespace Sms.Shared.Kernel.Auth;

/// Which sender id an outbound SMS should use. Null means the platform (Catre) sender.
public static class SchoolSmsRules
{
    public static string? Resolve(bool enabled, string? senderId)
    {
        if (!enabled) return null;
        var id = (senderId ?? "").Trim().ToUpperInvariant();
        if (id.Length != 6 || !id.All(char.IsLetterOrDigit)) return null;
        return id;
    }
}
