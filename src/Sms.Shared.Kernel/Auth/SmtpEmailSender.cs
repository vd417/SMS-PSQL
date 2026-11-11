using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Sms.Shared.Kernel.Auth;

/// Real SMTP delivery via MailKit. The only untested I/O shim — exercised by the EmailDispatchWorker.
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        var fromAddress = string.IsNullOrWhiteSpace(message.FromAddress) ? options.From : message.FromAddress.Trim();
        if (string.IsNullOrWhiteSpace(message.FromName))
            mime.From.Add(MailboxAddress.Parse(fromAddress));
        else
            mime.From.Add(new MailboxAddress(message.FromName.Trim(), fromAddress));
        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo.Trim()));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder { TextBody = message.Body };
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
            builder.HtmlBody = message.HtmlBody;
        if (message.AttachmentBytes is { Length: > 0 } bytes
            && !string.IsNullOrWhiteSpace(message.AttachmentFileName))
        {
            builder.Attachments.Add(
                message.AttachmentFileName,
                bytes,
                ContentType.Parse(message.AttachmentContentType ?? "application/pdf"));
        }
        if (message.ExtraAttachments is { Count: > 0 })
        {
            foreach (var a in message.ExtraAttachments)
            {
                if (a.Bytes is not { Length: > 0 }) continue;
                if (string.IsNullOrWhiteSpace(a.FileName)) continue;
                builder.Attachments.Add(
                    a.FileName,
                    a.Bytes,
                    ContentType.Parse(string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType));
            }
        }
        mime.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var schoolSmtp = !string.IsNullOrWhiteSpace(message.SmtpHost);
        var host = schoolSmtp ? message.SmtpHost! : options.Host;
        var port = schoolSmtp && message.SmtpPort > 0 ? message.SmtpPort : options.Port;
        var secure = schoolSmtp
            ? (message.SmtpSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
            : (options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        await client.ConnectAsync(host, port, secure, ct);
        var user = schoolSmtp ? message.SmtpUser : options.User;
        var password = schoolSmtp ? message.SmtpPassword : options.Password;
        if (!string.IsNullOrEmpty(user))
            await client.AuthenticateAsync(user, password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
