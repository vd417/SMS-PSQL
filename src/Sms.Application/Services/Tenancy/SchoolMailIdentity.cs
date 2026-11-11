using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Sms.Modules.Tenancy.Data;
using Sms.Shared.Kernel.Auth;

namespace Sms.Application.Services.Tenancy;

public sealed record SchoolEmailSettings(
    bool Enabled,
    string FromName,
    string FromAddress,
    string? ReplyTo,
    string? ReceiptTemplate,
    string? ReminderTemplate,
    string? OverdueTemplate,
    string Provider,
    string? SmtpHost,
    int SmtpPort,
    string Encryption,
    string? SmtpUsername,
    bool SmtpPasswordSet,
    bool NotifyLoginOtp,
    bool NotifyForgotOtp,
    bool NotifyFeeReceipt,
    bool NotifyFeeDue,
    bool NotifyFeeOverdue,
    bool NotifyGeneral,
    string Status,
    DateTime? LastTestAt,
    string? LastError);

public sealed record SaveSchoolEmail(
    bool Enabled,
    string FromName,
    string FromAddress,
    string? ReplyTo,
    string? ReceiptTemplate,
    string? ReminderTemplate,
    string? OverdueTemplate,
    string Provider,
    string? SmtpHost,
    int SmtpPort,
    string Encryption,
    string? SmtpUsername,
    string? SmtpPassword,
    bool NotifyLoginOtp,
    bool NotifyForgotOtp,
    bool NotifyFeeReceipt,
    bool NotifyFeeDue,
    bool NotifyFeeOverdue,
    bool NotifyGeneral);

public interface ISchoolMailIdentity
{
    /// School route when email is on, this notification is on, and SMTP is saved. Otherwise null (Catre server).
    Task<SchoolMailRoute?> ForTenantAsync(Guid? tenantId, CancellationToken ct = default);
    Task<SchoolMailRoute?> RouteAsync(Guid? tenantId, string emailType, CancellationToken ct = default);
    Task<SchoolEmailSettings> GetAsync(Guid tenantId, CancellationToken ct = default);
    Task<(SchoolEmailSettings Settings, string? Error)> SaveAsync(Guid tenantId, SaveSchoolEmail settings, CancellationToken ct = default);
    Task<(bool Ok, string? Error, DateTime? TestedAt)> TestConnectionAsync(Guid tenantId, CancellationToken ct = default);
    Task<(bool Ok, string? Error)> SendTestAsync(Guid tenantId, string to, string? template, CancellationToken ct = default);
}

public sealed class SchoolMailIdentity(
    TenantEmailSettingsRepository repo,
    IEmailSender email,
    IDataProtectionProvider dataProtection) : ISchoolMailIdentity
{
    private const string Purpose = "TenantEmail.SmtpPassword.v1";
    private IDataProtector Protector => dataProtection.CreateProtector(Purpose);

    public Task<SchoolMailRoute?> ForTenantAsync(Guid? tenantId, CancellationToken ct = default) =>
        RouteAsync(tenantId, "general", ct);

    public async Task<SchoolMailRoute?> RouteAsync(Guid? tenantId, string emailType, CancellationToken ct = default)
    {
        if (tenantId is not { } tid || tid == Guid.Empty) return null;
        var row = await repo.GetAsync(tid, ct);
        if (row is null) return null;
        var route = new SchoolMailRoute(tid, emailType, null, null);
        if (!row.EmailEnabled || !Allows(row, emailType)) return route;
        var from = SchoolMailFromRules.Resolve(true, row.EmailFromName, row.EmailFromAddress, row.EmailReplyTo);
        var smtp = OpenSmtp(row);
        if (from is null || smtp is null) return route;
        return route with { From = from, Smtp = smtp };
    }

    public async Task<SchoolEmailSettings> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);
        return row is null ? Empty() : ToSettings(row);
    }

    public async Task<(SchoolEmailSettings Settings, string? Error)> SaveAsync(
        Guid tenantId, SaveSchoolEmail input, CancellationToken ct = default)
    {
        var current = await repo.GetAsync(tenantId, ct);
        var password = (input.SmtpPassword ?? "").Trim();
        var hasNewPassword = password.Length > 0;
        string? encrypted = hasNewPassword ? Protector.Protect(password) : current?.SmtpPasswordEncrypted;
        var enabled = input.Enabled;
        string? error = null;
        var status = current?.EmailStatus ?? "not_configured";
        DateTime? testedAt = current?.EmailLastTestAt;
        string? lastError = current?.EmailLastError;

        if (enabled)
        {
            if (!SchoolMailFromRules.LooksLikeEmail(input.FromAddress))
                error = "Enter a from address like fees@school.edu.";
            else if (string.IsNullOrWhiteSpace(input.SmtpHost) || string.IsNullOrWhiteSpace(input.SmtpUsername))
                error = "Enter the SMTP host and username before turning school email on.";
            else if (!hasNewPassword && string.IsNullOrWhiteSpace(current?.SmtpPasswordEncrypted))
                error = "Enter the SMTP password before turning school email on.";
            else if (!SchoolMailFromRules.SenderAuthorized(input.Provider, input.FromAddress, input.SmtpUsername))
                error = "For Gmail, the from address must be the same as the SMTP username.";
            else
            {
                var secret = hasNewPassword ? password : Unprotect(current?.SmtpPasswordEncrypted);
                if (string.IsNullOrWhiteSpace(secret))
                    error = "The saved SMTP password could not be read. Enter it again.";
                else
                {
                    var probe = await SmtpProbe.TryAsync(new SmtpRoute(
                        input.SmtpHost.Trim(), input.SmtpPort <= 0 ? 587 : input.SmtpPort,
                        input.SmtpUsername.Trim(), secret, input.Encryption == "ssl"), ct);
                    if (!probe.Ok)
                    {
                        error = probe.Error ?? "SMTP connection failed.";
                        status = "failed";
                        lastError = Redact(error, secret);
                        enabled = false;
                    }
                    else
                    {
                        status = "connected";
                        testedAt = DateTime.UtcNow;
                        lastError = null;
                    }
                }
            }
            if (error is not null && status != "failed")
                enabled = false;
        }

        var row = new TenantEmailSettingsRow(
            enabled,
            Blank(input.FromName),
            Blank(input.FromAddress),
            Blank(input.ReplyTo),
            Blank(input.ReceiptTemplate),
            Blank(input.ReminderTemplate),
            Blank(input.OverdueTemplate),
            string.IsNullOrWhiteSpace(input.Provider) ? "custom" : input.Provider.Trim().ToLowerInvariant(),
            Blank(input.SmtpHost),
            input.SmtpPort <= 0 ? 587 : input.SmtpPort,
            input.Encryption == "ssl" ? "ssl" : "starttls",
            Blank(input.SmtpUsername),
            encrypted,
            input.NotifyLoginOtp,
            input.NotifyForgotOtp,
            input.NotifyFeeReceipt,
            input.NotifyFeeDue,
            input.NotifyFeeOverdue,
            input.NotifyGeneral,
            enabled ? status : (status == "failed" ? "failed" : "not_configured"),
            testedAt,
            lastError);
        await repo.SaveAsync(tenantId, row, hasNewPassword, ct);
        return (await GetAsync(tenantId, ct), error);
    }

    public async Task<(bool Ok, string? Error, DateTime? TestedAt)> TestConnectionAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);
        var smtp = row is null ? null : OpenSmtp(row);
        if (smtp is null)
        {
            await repo.MarkTestAsync(tenantId, "not_configured", "Save the SMTP host, username and password first.", null, ct);
            return (false, "Save the SMTP host, username and password first.", null);
        }
        var probe = await SmtpProbe.TryAsync(smtp, ct);
        var now = probe.Ok ? DateTime.UtcNow : (DateTime?)null;
        await repo.MarkTestAsync(tenantId, probe.Ok ? "connected" : "failed", probe.Ok ? null : Redact(probe.Error, smtp.Password), now, ct);
        return (probe.Ok, probe.Ok ? null : probe.Error, now);
    }

    public async Task<(bool Ok, string? Error)> SendTestAsync(Guid tenantId, string to, string? template, CancellationToken ct = default)
    {
        var recipient = (to ?? "").Trim();
        if (!SchoolMailFromRules.LooksLikeEmail(recipient))
            return (false, "Enter a recipient email address.");
        var route = await RouteAsync(tenantId, "general", ct);
        if (route is not { Smtp: not null, From: not null })
            return (false, "Turn on school email and save a working SMTP account before sending a test.");
        var body = SchoolEmailTemplate.Render(string.IsNullOrWhiteSpace(template)
            ? "This is a test email from {{school_name}}."
            : template, Sample());
        try
        {
            var message = new EmailMessage(recipient, "Test email", body, HtmlBody: body).WithRoute(route);
            await email.SendAsync(message, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, SmtpProbe.PublicMessage(ex));
        }
    }

    private static bool Allows(TenantEmailSettingsRow row, string emailType) => emailType switch
    {
        "login_otp" => row.NotifyLoginOtp,
        "forgot_otp" => row.NotifyForgotOtp,
        "fee_receipt" => row.NotifyFeeReceipt,
        "fee_due" => row.NotifyFeeDue,
        "fee_overdue" => row.NotifyFeeOverdue,
        _ => row.NotifyGeneral,
    };

    private SmtpRoute? OpenSmtp(TenantEmailSettingsRow row)
    {
        if (string.IsNullOrWhiteSpace(row.SmtpHost) || string.IsNullOrWhiteSpace(row.SmtpUsername)) return null;
        var secret = Unprotect(row.SmtpPasswordEncrypted);
        if (string.IsNullOrWhiteSpace(secret)) return null;
        return new SmtpRoute(row.SmtpHost.Trim(), row.SmtpPort <= 0 ? 587 : row.SmtpPort, row.SmtpUsername.Trim(), secret, row.SmtpEncryption == "ssl");
    }

    private string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return Protector.Unprotect(encrypted); }
        catch (CryptographicException) { return null; }
    }

    private static string Redact(string? error, string? secret)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "SMTP failed" : error.Trim();
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text.Length > 500 ? text[..500] : text;
    }

    private static SchoolEmailSettings ToSettings(TenantEmailSettingsRow row) => new(
        row.EmailEnabled,
        row.EmailFromName ?? "",
        row.EmailFromAddress ?? "",
        Blank(row.EmailReplyTo),
        Blank(row.EmailReceiptTemplate),
        Blank(row.EmailReminderTemplate),
        Blank(row.EmailOverdueTemplate),
        string.IsNullOrWhiteSpace(row.EmailProvider) ? "custom" : row.EmailProvider,
        Blank(row.SmtpHost),
        row.SmtpPort <= 0 ? 587 : row.SmtpPort,
        row.SmtpEncryption == "ssl" ? "ssl" : "starttls",
        Blank(row.SmtpUsername),
        !string.IsNullOrWhiteSpace(row.SmtpPasswordEncrypted),
        row.NotifyLoginOtp,
        row.NotifyForgotOtp,
        row.NotifyFeeReceipt,
        row.NotifyFeeDue,
        row.NotifyFeeOverdue,
        row.NotifyGeneral,
        string.IsNullOrWhiteSpace(row.EmailStatus) ? "not_configured" : row.EmailStatus,
        row.EmailLastTestAt,
        Blank(row.EmailLastError));

    private static SchoolEmailSettings Empty() => new(
        false, "", "", null, null, null, null, "custom", null, 587, "starttls", null, false,
        true, true, true, true, true, true, "not_configured", null, null);

    private static string? Blank(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static IReadOnlyDictionary<string, string> Sample() => new Dictionary<string, string>
    {
        ["school_name"] = "Riverdale School",
        ["student_name"] = "Aarav Sharma",
        ["parent_name"] = "Ravi Sharma",
        ["admission_number"] = "ADM1024",
        ["receipt_number"] = "RCP-1008",
        ["amount_paid"] = "5000",
        ["pending_amount"] = "2000",
        ["due_date"] = "15-11-2026",
        ["payment_date"] = "11-11-2026",
        ["payment_mode"] = "UPI",
        ["academic_year"] = "2026-27",
    };
}
