using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Finance;

public sealed record TenantPaymentCredentialRow(
    Guid TenantId, string Provider, string? KeyId, string? KeySecretEncrypted,
    string? WebhookSecretEncrypted, string Mode, bool IsEnabled);

public sealed class TenantPaymentCredentialRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<TenantPaymentCredentialRow?> GetAsync(Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<TenantPaymentCredentialRow>(
            "SELECT \"TenantId\", \"Provider\", \"KeyId\", \"KeySecretEncrypted\", \"WebhookSecretEncrypted\", \"Mode\", \"IsEnabled\" " +
            "FROM \"dbo\".\"TenantPaymentCredentials\" WHERE \"TenantId\" = @tenantId AND \"Provider\" = 'razorpay'",
            new { tenantId }, ct)).FirstOrDefault();

    public Task UpsertAsync(
        Guid tenantId, string? keyId, string? keySecretEncrypted, string? webhookSecretEncrypted,
        string? mode, bool? isEnabled, bool hasNewKeySecret, bool hasNewWebhookSecret, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            INSERT INTO "dbo"."TenantPaymentCredentials"
                ("TenantId", "Provider", "KeyId", "KeySecretEncrypted", "WebhookSecretEncrypted", "Mode", "IsEnabled")
            VALUES (@tenantId, 'razorpay', @keyId, @keySecretEncrypted, @webhookSecretEncrypted,
                COALESCE(@mode, 'test'), COALESCE(@isEnabled, false))
            ON CONFLICT ("TenantId") DO UPDATE SET
                "KeyId" = COALESCE(@keyId, "dbo"."TenantPaymentCredentials"."KeyId"),
                "KeySecretEncrypted" = CASE WHEN @hasNewKeySecret THEN @keySecretEncrypted ELSE "dbo"."TenantPaymentCredentials"."KeySecretEncrypted" END,
                "WebhookSecretEncrypted" = CASE WHEN @hasNewWebhookSecret THEN @webhookSecretEncrypted ELSE "dbo"."TenantPaymentCredentials"."WebhookSecretEncrypted" END,
                "Mode" = COALESCE(@mode, "dbo"."TenantPaymentCredentials"."Mode"),
                "IsEnabled" = COALESCE(@isEnabled, "dbo"."TenantPaymentCredentials"."IsEnabled"),
                "UpdatedAt" = now();
            """,
            new { tenantId, keyId, keySecretEncrypted, webhookSecretEncrypted, mode, isEnabled, hasNewKeySecret, hasNewWebhookSecret },
            ct);
}
