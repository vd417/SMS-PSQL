namespace Sms.Shared.Kernel.Auth;

/// Queues one SMTP message per unique recipient address (optional HTML + attachments).
public static class AnnouncementEmailDispatch
{
    public static int Enqueue(
        IEmailQueue queue,
        IEnumerable<string> recipients,
        string subject,
        string plainBody,
        string? htmlBody = null,
        byte[]? attachmentBytes = null,
        string? attachmentFileName = null,
        string? attachmentContentType = null,
        IReadOnlyList<EmailAttachment>? extraAttachments = null,
        SchoolMailRoute? sender = null)
    {
        var n = 0;
        var subj = string.IsNullOrWhiteSpace(subject) ? "[SchoolMate] Notice" : subject.Trim();
        foreach (var raw in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var to = (raw ?? "").Trim();
            if (to.Length < 3 || !to.Contains('@')) continue;
            queue.Enqueue(new EmailMessage(
                to,
                subj,
                plainBody,
                attachmentBytes,
                attachmentFileName,
                attachmentContentType ?? (attachmentBytes is { Length: > 0 } ? "application/pdf" : null),
                htmlBody,
                extraAttachments).WithRoute(sender));
            n++;
        }
        return n;
    }
}
