using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;

namespace Sms.Application.Services.Tenancy;

public sealed record SchoolSmsSettings(bool Enabled, string SenderId, string? ReceiptTemplate, string? ReminderTemplate);

public interface ISchoolSmsIdentity
{
    /// School sender id when SMS is on and the id is a 6-character DLT sender. Otherwise null (Catre).
    Task<string?> SenderForAsync(Guid? tenantId, CancellationToken ct = default);
    Task<SchoolSmsSettings> GetAsync(Guid tenantId, CancellationToken ct = default);
    Task<(SchoolSmsSettings Settings, string? Error)> SaveAsync(Guid tenantId, SchoolSmsSettings settings, CancellationToken ct = default);
}

public sealed class SchoolSmsIdentity(TenantSmsSettingsRepository repo) : ISchoolSmsIdentity
{
    public async Task<string?> SenderForAsync(Guid? tenantId, CancellationToken ct = default)
    {
        if (tenantId is not { } tid || tid == Guid.Empty) return null;
        var row = await repo.GetAsync(tid, ct);
        return row is null ? null : SchoolSmsRules.Resolve(row.SmsEnabled, row.SmsSenderId);
    }

    public async Task<SchoolSmsSettings> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);
        return row is null
            ? new SchoolSmsSettings(false, "", null, null)
            : new SchoolSmsSettings(row.SmsEnabled, row.SmsSenderId ?? "", row.SmsReceiptTemplate, row.SmsReminderTemplate);
    }

    public async Task<(SchoolSmsSettings Settings, string? Error)> SaveAsync(
        Guid tenantId, SchoolSmsSettings settings, CancellationToken ct = default)
    {
        var sender = (settings.SenderId ?? "").Trim().ToUpperInvariant();
        var enabled = settings.Enabled;
        string? error = null;
        if (enabled && SchoolSmsRules.Resolve(true, sender) is null)
        {
            enabled = false;
            error = "Turn on school SMS only with a 6-character sender ID such as SCHMAT.";
        }
        await repo.SaveAsync(tenantId, new TenantSmsSettingsRow(
            enabled,
            sender.Length == 0 ? null : sender,
            string.IsNullOrWhiteSpace(settings.ReceiptTemplate) ? null : settings.ReceiptTemplate.Trim(),
            string.IsNullOrWhiteSpace(settings.ReminderTemplate) ? null : settings.ReminderTemplate.Trim()), ct);
        return (await GetAsync(tenantId, ct), error);
    }
}
