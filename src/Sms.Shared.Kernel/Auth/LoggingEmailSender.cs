using Microsoft.Extensions.Logging;

namespace Sms.Shared.Kernel.Auth;

/// Development fallback when Smtp:Password is empty — logs the message and writes
/// attachments to a temp folder so downloads can be verified locally.
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sms-dev-emails");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var safeTo = string.Join("_", (message.To ?? "unknown").Split(Path.GetInvalidFileNameChars()));

        void WriteAtt(string? name, byte[]? bytes)
        {
            if (bytes is not { Length: > 0 } || string.IsNullOrWhiteSpace(name)) return;
            var safeName = string.Join("_", Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, $"{stamp}_{safeTo}_{safeName}");
            File.WriteAllBytes(path, bytes);
            logger.LogWarning("[DEV EMAIL ATTACHMENT] wrote {Path} ({Bytes} bytes)", path, bytes.Length);
        }

        WriteAtt(message.AttachmentFileName, message.AttachmentBytes);
        if (message.ExtraAttachments is { Count: > 0 })
        {
            foreach (var a in message.ExtraAttachments)
                WriteAtt(a.FileName, a.Bytes);
        }

        logger.LogWarning(
            "[DEV EMAIL] From={From} To={To} Subject={Subject} Primary={File} Extra={ExtraCount} BodyLen={BodyLen}",
            string.IsNullOrWhiteSpace(message.FromAddress) ? "(platform)" : message.FromAddress,
            message.To,
            message.Subject,
            message.AttachmentFileName ?? "(none)",
            message.ExtraAttachments?.Count ?? 0,
            message.Body?.Length ?? 0);
        return Task.CompletedTask;
    }
}
