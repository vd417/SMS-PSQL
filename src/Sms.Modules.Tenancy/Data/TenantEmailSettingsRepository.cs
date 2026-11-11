using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed record TenantEmailSettingsRow(
    bool EmailEnabled,
    string? EmailFromName,
    string? EmailFromAddress,
    string? EmailReplyTo,
    string? EmailReceiptTemplate,
    string? EmailReminderTemplate,
    string? EmailOverdueTemplate,
    string EmailProvider,
    string? SmtpHost,
    int SmtpPort,
    string SmtpEncryption,
    string? SmtpUsername,
    string? SmtpPasswordEncrypted,
    bool NotifyLoginOtp,
    bool NotifyForgotOtp,
    bool NotifyFeeReceipt,
    bool NotifyFeeDue,
    bool NotifyFeeOverdue,
    bool NotifyGeneral,
    string EmailStatus,
    DateTime? EmailLastTestAt,
    string? EmailLastError);

public sealed class TenantEmailSettingsRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string Columns = """
        "EmailEnabled", "EmailFromName", "EmailFromAddress", "EmailReplyTo",
        "EmailReceiptTemplate", "EmailReminderTemplate", "EmailOverdueTemplate",
        "EmailProvider", "SmtpHost", "SmtpPort", "SmtpEncryption", "SmtpUsername", "SmtpPasswordEncrypted",
        "NotifyLoginOtp", "NotifyForgotOtp", "NotifyFeeReceipt", "NotifyFeeDue", "NotifyFeeOverdue", "NotifyGeneral",
        "EmailStatus", "EmailLastTestAt", "EmailLastError"
        """;

    public async Task<TenantEmailSettingsRow?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<TenantEmailSettingsRow>(
            $"""SELECT {Columns} FROM "dbo"."Tenants" WHERE "Id" = @tenantId""",
            new { tenantId }, ct)).FirstOrDefault();

    public Task SaveAsync(Guid tenantId, TenantEmailSettingsRow row, bool hasNewPassword, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."Tenants" SET
                "EmailEnabled" = @EmailEnabled,
                "EmailFromName" = @EmailFromName,
                "EmailFromAddress" = @EmailFromAddress,
                "EmailReplyTo" = @EmailReplyTo,
                "EmailReceiptTemplate" = @EmailReceiptTemplate,
                "EmailReminderTemplate" = @EmailReminderTemplate,
                "EmailOverdueTemplate" = @EmailOverdueTemplate,
                "EmailProvider" = @EmailProvider,
                "SmtpHost" = @SmtpHost,
                "SmtpPort" = @SmtpPort,
                "SmtpEncryption" = @SmtpEncryption,
                "SmtpUsername" = @SmtpUsername,
                "SmtpPasswordEncrypted" = CASE WHEN @hasNewPassword THEN @SmtpPasswordEncrypted ELSE "SmtpPasswordEncrypted" END,
                "NotifyLoginOtp" = @NotifyLoginOtp,
                "NotifyForgotOtp" = @NotifyForgotOtp,
                "NotifyFeeReceipt" = @NotifyFeeReceipt,
                "NotifyFeeDue" = @NotifyFeeDue,
                "NotifyFeeOverdue" = @NotifyFeeOverdue,
                "NotifyGeneral" = @NotifyGeneral,
                "EmailStatus" = @EmailStatus,
                "EmailLastTestAt" = @EmailLastTestAt,
                "EmailLastError" = @EmailLastError
            WHERE "Id" = @tenantId
            """,
            new
            {
                tenantId,
                hasNewPassword,
                row.EmailEnabled,
                row.EmailFromName,
                row.EmailFromAddress,
                row.EmailReplyTo,
                row.EmailReceiptTemplate,
                row.EmailReminderTemplate,
                row.EmailOverdueTemplate,
                row.EmailProvider,
                row.SmtpHost,
                row.SmtpPort,
                row.SmtpEncryption,
                row.SmtpUsername,
                row.SmtpPasswordEncrypted,
                row.NotifyLoginOtp,
                row.NotifyForgotOtp,
                row.NotifyFeeReceipt,
                row.NotifyFeeDue,
                row.NotifyFeeOverdue,
                row.NotifyGeneral,
                row.EmailStatus,
                row.EmailLastTestAt,
                row.EmailLastError,
            },
            ct);

    public Task MarkTestAsync(Guid tenantId, string status, string? error, DateTime? testedAt, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."Tenants" SET
                "EmailStatus" = @status,
                "EmailLastError" = @error,
                "EmailLastTestAt" = COALESCE(@testedAt, "EmailLastTestAt")
            WHERE "Id" = @tenantId
            """,
            new { tenantId, status, error, testedAt }, ct);
}

public sealed class EmailDeliveryLogRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task InsertAsync(
        Guid tenantId, string recipient, string sender, string emailType, string subject,
        string status, int retryCount, string? error, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."EmailDeliveryLogs"
                ("TenantId", "Recipient", "Sender", "EmailType", "Subject", "Status", "RetryCount", "ErrorMessage")
            VALUES
                (@tenantId, @recipient, @sender, @emailType, @subject, @status, @retryCount, @error)
            """,
            new { tenantId, recipient, sender, emailType, subject, status, retryCount, error }, ct);
}
