using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Tenancy.Data;

public sealed record TenantSmsSettingsRow(
    bool SmsEnabled,
    string? SmsSenderId,
    string? SmsReceiptTemplate,
    string? SmsReminderTemplate);

public sealed class TenantSmsSettingsRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<TenantSmsSettingsRow?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<TenantSmsSettingsRow>(
            """
            SELECT "SmsEnabled", "SmsSenderId", "SmsReceiptTemplate", "SmsReminderTemplate"
            FROM "dbo"."Tenants" WHERE "Id" = @tenantId
            """,
            new { tenantId }, ct)).FirstOrDefault();

    public Task SaveAsync(Guid tenantId, TenantSmsSettingsRow row, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."Tenants" SET
                "SmsEnabled" = @SmsEnabled,
                "SmsSenderId" = @SmsSenderId,
                "SmsReceiptTemplate" = @SmsReceiptTemplate,
                "SmsReminderTemplate" = @SmsReminderTemplate
            WHERE "Id" = @tenantId
            """,
            new { tenantId, row.SmsEnabled, row.SmsSenderId, row.SmsReceiptTemplate, row.SmsReminderTemplate }, ct);
}
