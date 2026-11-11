using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sms.Application.Services.Finance;
using Sms.Application.Services.Tenancy;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Http;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Controllers;

public sealed record EmailSettingsBody(
    bool? Enabled, string? FromName, string? FromAddress, string? ReplyTo,
    string? ReceiptTemplate, string? ReminderTemplate, string? OverdueTemplate,
    string? Provider, string? SmtpHost, int? SmtpPort, string? Encryption, string? SmtpUsername, string? SmtpPassword,
    bool? NotifyLoginOtp, bool? NotifyForgotOtp, bool? NotifyFeeReceipt, bool? NotifyFeeDue, bool? NotifyFeeOverdue, bool? NotifyGeneral);
public sealed record TestEmailBody(string? To, string? Template);
public sealed record SmsSettingsBody(bool? Enabled, string? SenderId, string? ReceiptTemplate, string? ReminderTemplate);
public sealed record RazorpaySettingsBody(string? KeyId, string? KeySecret, string? WebhookSecret, string? Mode, bool? Enabled);
public sealed record SaveIntegrationsBody(EmailSettingsBody? Email, SmsSettingsBody? Sms, RazorpaySettingsBody? Razorpay);

[Route("v1/school/integrations")]
[Authorize]
public sealed class SchoolIntegrationsController(
    ITenantPaymentCredentialService credentials,
    ISchoolMailIdentity mail,
    ISchoolSmsIdentity sms,
    IRazorpayClient razorpay,
    ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return ForbiddenResult("no tenant context");
        var status = await credentials.GetStatusAsync(tid, ct);
        var email = await mail.GetAsync(tid, ct);
        var smsSettings = await sms.GetAsync(tid, ct);
        return Ok(new
        {
            data = new
            {
                email = new
                {
                    enabled = email.Enabled,
                    from_name = email.FromName,
                    from_address = email.FromAddress,
                    reply_to = email.ReplyTo ?? "",
                    receipt_template = email.ReceiptTemplate ?? "",
                    reminder_template = email.ReminderTemplate ?? "",
                    overdue_template = email.OverdueTemplate ?? "",
                    provider = email.Provider,
                    smtp_host = email.SmtpHost ?? "",
                    smtp_port = email.SmtpPort,
                    encryption = email.Encryption,
                    smtp_username = email.SmtpUsername ?? "",
                    smtp_password_set = email.SmtpPasswordSet,
                    notify_login_otp = email.NotifyLoginOtp,
                    notify_forgot_otp = email.NotifyForgotOtp,
                    notify_fee_receipt = email.NotifyFeeReceipt,
                    notify_fee_due = email.NotifyFeeDue,
                    notify_fee_overdue = email.NotifyFeeOverdue,
                    notify_general = email.NotifyGeneral,
                    status = email.Status,
                    last_test_at = email.LastTestAt,
                    last_error = email.LastError ?? "",
                },
                sms = new
                {
                    enabled = smsSettings.Enabled,
                    sender_id = smsSettings.SenderId,
                    receipt_template = smsSettings.ReceiptTemplate ?? "",
                    reminder_template = smsSettings.ReminderTemplate ?? "",
                },
                razorpay = new
                {
                    enabled = status.Enabled,
                    key_id = status.KeyId ?? "",
                    mode = status.Mode,
                    status = status.Status,
                    key_secret_set = status.KeySecretSet,
                    webhook_secret_set = status.WebhookSecretSet,
                },
            },
        });
    }

    [HttpPut("")]
    [Authorize(Policy = Policies.SchoolOwnerOnly)]
    public async Task<IActionResult> Save([FromBody] SaveIntegrationsBody body, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return ForbiddenResult("no tenant context");
        if (body.Email is { } emailBody)
        {
            var saved = await mail.SaveAsync(tid, new SaveSchoolEmail(
                emailBody.Enabled ?? false,
                emailBody.FromName ?? "",
                (emailBody.FromAddress ?? "").Trim(),
                emailBody.ReplyTo,
                emailBody.ReceiptTemplate,
                emailBody.ReminderTemplate,
                emailBody.OverdueTemplate,
                emailBody.Provider ?? "custom",
                emailBody.SmtpHost,
                emailBody.SmtpPort ?? 587,
                emailBody.Encryption ?? "starttls",
                emailBody.SmtpUsername,
                emailBody.SmtpPassword,
                emailBody.NotifyLoginOtp ?? true,
                emailBody.NotifyForgotOtp ?? true,
                emailBody.NotifyFeeReceipt ?? true,
                emailBody.NotifyFeeDue ?? true,
                emailBody.NotifyFeeOverdue ?? true,
                emailBody.NotifyGeneral ?? true), ct);
            if (saved.Error is not null)
                return StatusCode(422, ErrorEnvelope.From(new Error("invalid_email", saved.Error)));
        }
        if (body.Sms is { } smsBody)
        {
            var savedSms = await sms.SaveAsync(tid, new SchoolSmsSettings(
                smsBody.Enabled ?? false,
                smsBody.SenderId ?? "",
                smsBody.ReceiptTemplate,
                smsBody.ReminderTemplate), ct);
            if (savedSms.Error is not null)
                return StatusCode(422, ErrorEnvelope.From(new Error("invalid_sms", savedSms.Error)));
        }
        if (body.Razorpay is { } r)
            // Mode/Enabled are passed through as-given (null = omitted, leave unchanged) — the
            // repository's MERGE COALESCEs them against the stored row, exactly like KeyId already
            // does, so a partial body (per spec, and sms-admin's Partial<...> client type) never
            // silently resets Mode to "test" or disables the integration.
            await credentials.UpsertAsync(tid, new UpsertTenantRazorpayRequest(
                r.KeyId, r.KeySecret, r.WebhookSecret, r.Mode, r.Enabled), ct);
        return await Get(ct);
    }

    [HttpPost("razorpay/verify")]
    [Authorize(Policy = Policies.SchoolOwnerOnly)]
    [EnableRateLimiting("email-test")]
    public async Task<IActionResult> VerifyCredentials(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return ForbiddenResult("no tenant context");
        var creds = await credentials.GetActiveAsync(tid, ct);
        if (creds is null)
            return StatusCode(422, ErrorEnvelope.From(new Error(
                "razorpay_not_configured",
                "Turn on Razorpay and save this school's key ID and secret. Parent fees are not collected into Catre's account.")));
        var probe = await razorpay.ProbeAsync(creds.KeyId, creds.KeySecret, ct);
        if (!probe.Ok)
            return StatusCode(422, ErrorEnvelope.From(new Error("razorpay_invalid", probe.Error ?? "Razorpay rejected these keys.")));
        return Ok(new { data = new { status = "configured" } });
    }

    [HttpPost("email/test-connection")]
    [Authorize(Policy = Policies.SchoolOwnerOnly)]
    [EnableRateLimiting("email-test")]
    public async Task<IActionResult> TestEmailConnection(CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return ForbiddenResult("no tenant context");
        var result = await mail.TestConnectionAsync(tid, ct);
        if (!result.Ok)
            return StatusCode(422, ErrorEnvelope.From(new Error("smtp_failed", result.Error ?? "SMTP connection failed.")));
        return Ok(new { data = new { status = "connected", last_test_at = result.TestedAt } });
    }

    [HttpPost("email/test-send")]
    [Authorize(Policy = Policies.SchoolOwnerOnly)]
    [EnableRateLimiting("email-test")]
    public async Task<IActionResult> SendTestEmail([FromBody] TestEmailBody body, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tid)
            return ForbiddenResult("no tenant context");
        var result = await mail.SendTestAsync(tid, body.To ?? "", body.Template, ct);
        if (!result.Ok)
            return StatusCode(422, ErrorEnvelope.From(new Error("smtp_failed", result.Error ?? "Test email failed.")));
        return Ok(new { data = new { sent = true } });
    }
}
