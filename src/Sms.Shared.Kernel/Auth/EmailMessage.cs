namespace Sms.Shared.Kernel.Auth;

public sealed record EmailAttachment(
    byte[] Bytes,
    string FileName,
    string ContentType = "application/octet-stream");

/// A queued email to be delivered out-of-band by the EmailDispatchWorker.
public sealed record EmailMessage(
    string To,
    string Subject,
    string Body,
    byte[]? AttachmentBytes = null,
    string? AttachmentFileName = null,
    string? AttachmentContentType = null,
    string? HtmlBody = null,
    IReadOnlyList<EmailAttachment>? ExtraAttachments = null,
    string? FromName = null,
    string? FromAddress = null,
    string? ReplyTo = null,
    string? SmtpHost = null,
    int SmtpPort = 0,
    string? SmtpUser = null,
    string? SmtpPassword = null,
    bool SmtpSsl = false,
    Guid? TenantId = null,
    string? EmailType = null,
    int Attempt = 1)
{
    public EmailMessage WithSender(SchoolMailFrom? from) =>
        from is null ? this : this with { FromName = from.Name, FromAddress = from.Address, ReplyTo = from.ReplyTo };

    public EmailMessage WithRoute(SchoolMailRoute? route)
    {
        if (route is null) return this;
        var next = this with { TenantId = route.TenantId, EmailType = route.EmailType };
        if (route.From is { } from)
            next = next with { FromName = from.Name, FromAddress = from.Address, ReplyTo = from.ReplyTo };
        if (route.Smtp is { } smtp)
            next = next with
            {
                SmtpHost = smtp.Host,
                SmtpPort = smtp.Port,
                SmtpUser = smtp.User,
                SmtpPassword = smtp.Password,
                SmtpSsl = smtp.Ssl,
            };
        return next;
    }
}
