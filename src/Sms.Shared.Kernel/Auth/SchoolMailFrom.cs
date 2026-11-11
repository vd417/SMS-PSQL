namespace Sms.Shared.Kernel.Auth;

/// The From identity for one outbound message. Null means "use the platform (Catre) mailbox".
public sealed record SchoolMailFrom(string? Name, string Address, string? ReplyTo);

/// School SMTP account. Present only in memory for the send. Never written to logs.
public sealed record SmtpRoute(string Host, int Port, string User, string Password, bool Ssl);

/// How one school message should leave. Smtp null means the platform (Catre) server.
public sealed record SchoolMailRoute(Guid TenantId, string EmailType, SchoolMailFrom? From, SmtpRoute? Smtp);

public static class SchoolMailFromRules
{
    /// A school mailbox is used only when email is turned on and the from address looks like an email.
    public static SchoolMailFrom? Resolve(bool enabled, string? name, string? address, string? replyTo)
    {
        if (!enabled) return null;
        var addr = (address ?? "").Trim();
        if (!LooksLikeEmail(addr)) return null;
        var reply = (replyTo ?? "").Trim();
        if (!LooksLikeEmail(reply)) reply = "";
        var display = (name ?? "").Trim();
        return new SchoolMailFrom(display.Length == 0 ? null : display, addr, reply.Length == 0 ? null : reply);
    }

    /// Gmail and Google Workspace reject a From address that is not the authenticated mailbox.
    public static bool SenderAuthorized(string? provider, string? fromAddress, string? smtpUsername)
    {
        var kind = (provider ?? "").Trim().ToLowerInvariant();
        if (kind is not ("gmail" or "workspace")) return true;
        var from = (fromAddress ?? "").Trim();
        var user = (smtpUsername ?? "").Trim();
        return from.Length > 0 && string.Equals(from, user, StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ');
    }
}

/// Request-scoped sender chosen before an OTP is queued. The OTP sender is a singleton,
/// so the school mailbox travels on the async call instead of through the container.
public static class OutboundMail
{
    private static readonly AsyncLocal<SchoolMailRoute?> Slot = new();

    public static SchoolMailRoute? Current => Slot.Value;

    public static IDisposable Use(SchoolMailRoute? route)
    {
        var prior = Slot.Value;
        Slot.Value = route;
        return new Pop(prior);
    }

    private sealed class Pop(SchoolMailRoute? prior) : IDisposable
    {
        public void Dispose() => Slot.Value = prior;
    }
}
