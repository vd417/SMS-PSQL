using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Tenancy;

/// Writes one delivery row per school send. The password is never stored in the log.
public sealed class SchoolEmailAuditSender(
    SmtpEmailSender inner,
    IServiceScopeFactory scopes,
    ILogger<SchoolEmailAuditSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        try
        {
            await inner.SendAsync(message, ct);
            await WriteAsync(message, "sent", null, ct);
        }
        catch (Exception ex)
        {
            await WriteAsync(message, "failed", SmtpProbe.PublicMessage(ex), ct);
            throw;
        }
    }

    private async Task WriteAsync(EmailMessage message, string status, string? error, CancellationToken ct)
    {
        if (message.TenantId is not { } tenantId || tenantId == Guid.Empty) return;
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, false);
            var text = error;
            if (!string.IsNullOrEmpty(message.SmtpPassword) && text is not null)
                text = text.Replace(message.SmtpPassword, "***", StringComparison.Ordinal);
            if (text is { Length: > 500 }) text = text[..500];
            await scope.ServiceProvider.GetRequiredService<EmailDeliveryLogRepository>().InsertAsync(
                tenantId,
                message.To ?? "",
                string.IsNullOrWhiteSpace(message.FromAddress) ? "catre" : message.FromAddress,
                string.IsNullOrWhiteSpace(message.EmailType) ? "general" : message.EmailType,
                Trim(message.Subject, 300),
                status,
                Math.Max(0, message.Attempt - 1),
                text,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the email delivery log for {To}", message.To);
        }
    }

    private static string Trim(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
