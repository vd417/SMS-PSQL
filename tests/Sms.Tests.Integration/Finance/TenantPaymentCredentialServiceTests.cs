using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Application.Services.Finance;
using Sms.Modules.Finance;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class TenantPaymentCredentialServiceTests(PostgresFixture fx)
{
    private static ITenantPaymentCredentialService BuildService(PostgresFixture fx)
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var repo = new TenantPaymentCredentialRepository(factory);
        var provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>();
        return new TenantPaymentCredentialService(repo, protector);
    }

    [Fact]
    public async Task Upsert_then_GetActive_round_trips_the_secret_decrypted()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);

        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            KeyId: "rzp_test_abc", KeySecret: "top-secret-value", WebhookSecret: "webhook-secret-value",
            Mode: "test", IsEnabled: true), CancellationToken.None);

        var active = await svc.GetActiveAsync(tenantId, CancellationToken.None);
        active.Should().NotBeNull();
        active!.KeyId.Should().Be("rzp_test_abc");
        active.KeySecret.Should().Be("top-secret-value");
        active.WebhookSecret.Should().Be("webhook-secret-value");
        active.Mode.Should().Be("test");

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        // dbo.TenantPaymentCredentials is tenant-RLS-scoped (M0190) — a bare connection has no
        // session context, so stamp the tenant explicitly for this raw read.
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        var storedSecret = await conn.QuerySingleAsync<string>(
            "SELECT KeySecretEncrypted FROM dbo.TenantPaymentCredentials WHERE TenantId = @tenantId", new { tenantId });
        storedSecret.Should().NotBe("top-secret-value"); // must be encrypted at rest, not plaintext
    }

    [Fact]
    public async Task GetActive_returns_null_when_disabled()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_x", "secret", "whsecret", "test", IsEnabled: false), CancellationToken.None);

        (await svc.GetActiveAsync(tenantId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetActive_returns_null_when_never_configured()
    {
        var svc = BuildService(fx);
        (await svc.GetActiveAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_omitting_secret_leaves_stored_secret_unchanged()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_x", "original-secret", "original-webhook", "test", true), CancellationToken.None);

        // Second upsert changes only KeyId, omits both secrets (null = "leave unchanged")
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_y", null, null, "test", true), CancellationToken.None);

        var active = await svc.GetActiveAsync(tenantId, CancellationToken.None);
        active!.KeyId.Should().Be("rzp_test_y");
        active.KeySecret.Should().Be("original-secret");
        active.WebhookSecret.Should().Be("original-webhook");
    }

    [Fact]
    public async Task Upsert_with_empty_string_secret_clears_the_stored_value()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_clear", "original-secret", "original-webhook", "test", true), CancellationToken.None);

        // Explicit "" (not null/omitted) must clear the secret, not leave it unchanged.
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_clear", "", "", "test", true), CancellationToken.None);

        var status = await svc.GetStatusAsync(tenantId, CancellationToken.None);
        status.KeySecretSet.Should().BeFalse();
        status.WebhookSecretSet.Should().BeFalse();

        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        var (keySecret, webhookSecret) = (await conn.QuerySingleAsync<(string? KeySecretEncrypted, string? WebhookSecretEncrypted)>(
            "SELECT KeySecretEncrypted, WebhookSecretEncrypted FROM dbo.TenantPaymentCredentials WHERE TenantId = @tenantId",
            new { tenantId }));
        keySecret.Should().BeNull();
        webhookSecret.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_omitting_mode_and_enabled_leaves_them_unchanged()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_partial", "secret", "whsecret", "live", true), CancellationToken.None);

        // Partial body: only KeyId given, Mode/IsEnabled omitted (null) — must not reset to
        // defaults ("test"/false).
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_partial_v2", null, null, null, null), CancellationToken.None);

        var status = await svc.GetStatusAsync(tenantId, CancellationToken.None);
        status.KeyId.Should().Be("rzp_test_partial_v2");
        status.Mode.Should().Be("live");
        status.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Status_is_not_configured_when_webhook_secret_is_missing()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var svc = BuildService(fx);

        // KeyId + KeySecret set, but WebhookSecret never provided — every webhook delivery would
        // fail closed (400) forever with no other signal, so this must not report "configured".
        await svc.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
            "rzp_test_nowh", "secret", null, "test", true), CancellationToken.None);

        var status = await svc.GetStatusAsync(tenantId, CancellationToken.None);
        status.Status.Should().Be("not_configured");
        status.KeySecretSet.Should().BeTrue();
        status.WebhookSecretSet.Should().BeFalse();
    }

    [Fact]
    public async Task Keys_persisted_to_the_filesystem_survive_a_fresh_provider_built_from_the_same_path()
    {
        // Simulates a redeploy/restart/scale-out: a brand-new IDataProtectionProvider instance,
        // pointed at the same on-disk key ring path, must still be able to decrypt a secret that
        // was encrypted by a previous provider instance — proving PersistKeysToFileSystem actually
        // makes the key ring durable across process lifetimes, rather than the default ephemeral/
        // in-memory-only key ring AddDataProtection() uses with no persistence configured.
        var keyPath = Path.Combine(Path.GetTempPath(), "sms-dp-keys-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyPath);
        try
        {
            var tenantId = Guid.NewGuid();
            await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

            TenantPaymentCredentialService BuildWithFileSystemProvider()
            {
                var ctx = new TenantContext();
                ctx.Set(null, Guid.NewGuid(), true);
                var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
                var repo = new TenantPaymentCredentialRepository(factory);
                var provider = new ServiceCollection()
                    .AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
                    .Services.BuildServiceProvider();
                return new TenantPaymentCredentialService(repo, provider.GetRequiredService<IDataProtectionProvider>());
            }

            var firstProcess = BuildWithFileSystemProvider();
            await firstProcess.UpsertAsync(tenantId, new UpsertTenantRazorpayRequest(
                "rzp_test_restart", "secret-that-must-survive-restart", "webhook-secret", "test", true),
                CancellationToken.None);

            // A brand-new provider instance = a brand-new in-memory key cache, reading whatever is
            // on disk at keyPath — exactly what happens on the next process after a restart.
            var afterRestart = BuildWithFileSystemProvider();
            var active = await afterRestart.GetActiveAsync(tenantId, CancellationToken.None);

            active.Should().NotBeNull("the key ring must be persisted to disk, not held only in the first process's memory");
            active!.KeySecret.Should().Be("secret-that-must-survive-restart");
            active.WebhookSecret.Should().Be("webhook-secret");
        }
        finally
        {
            Directory.Delete(keyPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetActive_returns_null_instead_of_throwing_when_the_encrypted_secret_cannot_be_decrypted()
    {
        // Simulates the key ring having been lost (e.g. an un-persisted/ephemeral key ring wiped by a
        // restart) while old ciphertext is still sitting in the database: Unprotect() throws
        // CryptographicException. We reproduce the same throw deterministically by encrypting under a
        // *different* DataProtection purpose than TenantPaymentCredentialService uses — the payload is
        // validly formatted but fails the purpose/subkey validation on Unprotect, exactly like ciphertext
        // whose original key is gone. The fail-closed contract is that this must degrade to "not
        // configured" (null), not propagate as an uncaught 500 on the next webhook/verify call.
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        var wrongPurposeProtector = provider.CreateProtector("Some.Unrelated.Purpose.v1");
        var undecryptableSecret = wrongPurposeProtector.Protect("irrelevant-payload");

        await using (var conn = new SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TenantPaymentCredentials (TenantId, Provider, KeyId, KeySecretEncrypted, WebhookSecretEncrypted, Mode, IsEnabled) " +
                "VALUES (@tenantId, 'razorpay', 'rzp_test_corrupt', @undecryptableSecret, @undecryptableSecret, 'test', 1)",
                new { tenantId, undecryptableSecret });
        }

        var svc = new TenantPaymentCredentialService(
            new TenantPaymentCredentialRepository(new NpgsqlConnectionFactory(fx.ConnectionString, PlatformCtx())), provider);
        var active = await svc.GetActiveAsync(tenantId, CancellationToken.None);

        active.Should().BeNull("an undecryptable secret must present as 'not configured', not throw");
    }

    private static TenantContext PlatformCtx()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        return ctx;
    }
}
