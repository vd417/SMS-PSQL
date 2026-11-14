using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Sms.Modules.Finance;

namespace Sms.Application.Services.Finance;

public sealed record TenantRazorpayCredentials(string KeyId, string KeySecret, string WebhookSecret, string Mode);

/// KeyId/Mode/IsEnabled: null means "omitted, leave unchanged" (partial-body PUT support).
/// KeySecret/WebhookSecret: null means "omitted, leave unchanged"; an explicit empty string
/// ("") means "clear it" — the stored encrypted value is set back to NULL, not re-encrypted
/// as an empty string.
public sealed record UpsertTenantRazorpayRequest(string? KeyId, string? KeySecret, string? WebhookSecret, string? Mode, bool? IsEnabled);
public sealed record TenantRazorpayCredentialStatus(
    bool Enabled, string? KeyId, string Mode, string Status, bool KeySecretSet, bool WebhookSecretSet);

public interface ITenantPaymentCredentialService
{
    Task<TenantRazorpayCredentials?> GetActiveAsync(Guid tenantId, CancellationToken ct = default);
    Task<TenantRazorpayCredentialStatus> UpsertAsync(Guid tenantId, UpsertTenantRazorpayRequest req, CancellationToken ct = default);
    Task<TenantRazorpayCredentialStatus> GetStatusAsync(Guid tenantId, CancellationToken ct = default);
}

public sealed class TenantPaymentCredentialService(
    TenantPaymentCredentialRepository repo, IDataProtectionProvider dataProtection) : ITenantPaymentCredentialService
{
    private const string Purpose = "TenantPaymentCredentials.Razorpay.v1";
    private IDataProtector Protector => dataProtection.CreateProtector(Purpose);

    public async Task<TenantRazorpayCredentials?> GetActiveAsync(Guid tenantId, CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);
        if (row is null || !row.IsEnabled || row.KeyId is null || row.KeySecretEncrypted is null)
            return null;
        try
        {
            return new TenantRazorpayCredentials(
                row.KeyId,
                Protector.Unprotect(row.KeySecretEncrypted),
                row.WebhookSecretEncrypted is null ? "" : Protector.Unprotect(row.WebhookSecretEncrypted),
                row.Mode);
        }
        catch (CryptographicException)
        {
            // The DataProtection key ring that encrypted this secret is gone (or never persisted —
            // e.g. a restart/redeploy/scale-out lost it). Treat this exactly like "not configured"
            // so callers fall back to their existing clean "payment_gateway_not_configured"/403
            // error instead of an uncaught 500 on every webhook/verify/order-create call.
            return null;
        }
    }

    public async Task<TenantRazorpayCredentialStatus> UpsertAsync(
        Guid tenantId, UpsertTenantRazorpayRequest req, CancellationToken ct = default)
    {
        // null = omitted (leave unchanged); "" = explicit clear (store NULL); non-empty = new value.
        var hasNewKeySecret = req.KeySecret is not null;
        var hasNewWebhookSecret = req.WebhookSecret is not null;
        await repo.UpsertAsync(
            tenantId, req.KeyId,
            hasNewKeySecret ? (req.KeySecret!.Length == 0 ? null : Protector.Protect(req.KeySecret)) : null,
            hasNewWebhookSecret ? (req.WebhookSecret!.Length == 0 ? null : Protector.Protect(req.WebhookSecret)) : null,
            req.Mode, req.IsEnabled, hasNewKeySecret, hasNewWebhookSecret, ct);
        return await GetStatusAsync(tenantId, ct);
    }

    public async Task<TenantRazorpayCredentialStatus> GetStatusAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);

        if (row is null)
            return new TenantRazorpayCredentialStatus(
                false, null, "test", "not_configured", false, false);

        var keySecretSet = !string.IsNullOrWhiteSpace(row.KeySecretEncrypted);
        var webhookSecretSet = !string.IsNullOrWhiteSpace(row.WebhookSecretEncrypted);
        var configured = false;

        if (row.IsEnabled &&
            !string.IsNullOrWhiteSpace(row.KeyId) &&
            keySecretSet && webhookSecretSet)
        {
            try
            {
                var protector = Protector;
                var keySecret = protector.Unprotect(row.KeySecretEncrypted!);
                var webhookSecret = protector.Unprotect(row.WebhookSecretEncrypted!);

                configured = !string.IsNullOrWhiteSpace(keySecret) &&
                             !string.IsNullOrWhiteSpace(webhookSecret);
            }
            catch (CryptographicException)
            {
                configured = false;
            }
        }

        return new TenantRazorpayCredentialStatus(
            row.IsEnabled, row.KeyId, row.Mode,
            configured ? "configured" : "not_configured",
            keySecretSet, webhookSecretSet);
    }
}
