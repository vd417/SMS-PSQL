using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class RazorpayFeeWebhookTests(PostgresFixture fx)
{
    // >= 32 bytes, required by Sms.Shared.Kernel.Configuration.SecretsValidator at host startup —
    // this endpoint is [AllowAnonymous] but the host still validates Jwt:SigningKey is configured.
    private const string SigningKey = "integration-test-signing-key-32-bytes-min!!";

    private sealed class FakeRazorpayClient(bool signatureValid) : IRazorpayClient
    {
        public Task<RazorpayOrderCreated> CreateOrderAsync(
            string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default) =>
            Task.FromResult(new RazorpayOrderCreated("unused", amountPaise, currency));
        public bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature) => signatureValid;
        public bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader) => signatureValid;
    }

    private static WebApplicationFactory<Program> App(PostgresFixture fx, bool signatureValid = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", SigningKey);
            b.ConfigureTestServices(services => services.AddSingleton<IRazorpayClient>(new FakeRazorpayClient(signatureValid)));
        });

    private static async Task<(Guid tenantId, Guid principalUserId, Guid invoiceId, string orderId)> SeedOrderAsync(
        WebApplicationFactory<Program> app, PostgresFixture fx)
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var orderId = $"order_webhook_{Guid.NewGuid():N}";
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Status\", \"Grade\") " +
            "VALUES (@studentId, @tenantId, 'A400', 'Webhook Kid', 'active', '8')", new { studentId, tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"FeeInvoices\" (\"Id\", \"TenantId\", \"StudentId\", \"Period\", \"Amount\", \"PaidAmount\", \"Status\") " +
            "VALUES (@invoiceId, @tenantId, @studentId, 'Term 1', 2000, 0, 'due')", new { invoiceId, tenantId, studentId });
        // GetActiveAsync unconditionally Unprotect()s these — must be produced via the app's own
        // IDataProtectionProvider under the same purpose TenantPaymentCredentialService uses, or it throws.
        using var scope = app.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("TenantPaymentCredentials.Razorpay.v1");
        var keySecretEncrypted = protector.Protect("irrelevant-for-this-test-fake-client");
        var webhookSecretEncrypted = protector.Protect("irrelevant-webhook-secret-fake-client");
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"TenantPaymentCredentials\" (\"TenantId\", \"Provider\", \"KeyId\", \"KeySecretEncrypted\", \"WebhookSecretEncrypted\", \"Mode\", \"IsEnabled\") " +
            "VALUES (@tenantId, 'razorpay', 'rzp_test_wh', @keySecretEncrypted, @webhookSecretEncrypted, 'test', true)",
            new { tenantId, keySecretEncrypted, webhookSecretEncrypted });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"FeePaymentOrders\" (\"Id\", \"TenantId\", \"InvoiceId\", \"RazorpayOrderId\", \"AmountPaise\", \"Status\", \"InitiatedBy\") " +
            "VALUES (gen_random_uuid(), @tenantId, @invoiceId, @orderId, 200000, 'Created', 'parent')", new { tenantId, invoiceId, orderId });
        return (tenantId, principalUserId, invoiceId, orderId);
    }

    private static StringContent WebhookBody(string orderId, string paymentId, string evt = "payment.captured") =>
        new(JsonSerializer.Serialize(new
        {
            @event = evt,
            payload = new { payment = new { entity = new { order_id = orderId, id = paymentId } } },
        }), Encoding.UTF8, "application/json");

    // Mirrors RazorpayVerifyPaymentTests.AuthedClient — same signing key, same JwtTokenService shape,
    // so a genuine cross-path (Task 5 verify + Task 6 webhook) scenario can be exercised in this file.
    private static HttpClient AuthedClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = SigningKey, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, Guid invoiceId, string orderId, string paymentId) =>
        client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/razorpay/verify", new
        {
            razorpay_order_id = orderId, razorpay_payment_id = paymentId, razorpay_signature = "any-value-fake-client-accepts-everything",
        });

    [Fact]
    public async Task Payment_captured_webhook_records_payment_without_any_client_confirm()
    {
        await using var app = App(fx);
        var (tenantId, _, invoiceId, orderId) = await SeedOrderAsync(app, fx);
        var client = app.CreateClient();

        var res = await client.PostAsync("/v1/webhooks/razorpay-fees",
            WebhookBody(orderId, "pay_WEBHOOK_ONLY"));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var status = await conn.QuerySingleAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"FeeInvoices\" WHERE \"Id\" = @invoiceId", new { invoiceId });
        status.Should().Be("paid");
    }

    [Fact]
    public async Task Duplicate_webhook_delivery_does_not_duplicate_payment()
    {
        await using var app = App(fx);
        var (tenantId, _, invoiceId, orderId) = await SeedOrderAsync(app, fx);
        var client = app.CreateClient();
        const string paymentId = "pay_RACE";

        var first = await client.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody(orderId, paymentId));
        var second = await client.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody(orderId, paymentId));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(1);
    }

    // This is the actual Task-5-verify-then-Task-6-webhook race the brief's Step 4 shared-helper
    // extraction exists to protect against: the client's browser calls /razorpay/verify right after
    // Razorpay's checkout.js redirect completes, and Razorpay's own webhook for the same payment can
    // land moments later (or even first, if the client's tab closes before the verify call returns).
    // Both paths must derive the same idempotency Guid from the same razorpay_payment_id so the
    // (TenantId, IdempotencyKey) unique constraint — not luck — is what prevents a double-charge.
    [Fact]
    public async Task Verify_then_webhook_for_same_payment_does_not_duplicate()
    {
        await using var app = App(fx);
        var (tenantId, principalUserId, invoiceId, orderId) = await SeedOrderAsync(app, fx);
        const string paymentId = "pay_CROSSPATH_VW";

        var authedClient = AuthedClient(app, tenantId, principalUserId, "principal");
        var verifyRes = await VerifyAsync(authedClient, invoiceId, orderId, paymentId);
        verifyRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var anonClient = app.CreateClient();
        var webhookRes = await anonClient.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody(orderId, paymentId));
        webhookRes.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(1);
        var status = await conn.QuerySingleAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"FeeInvoices\" WHERE \"Id\" = @invoiceId", new { invoiceId });
        status.Should().Be("paid");
    }

    // The reverse ordering: Razorpay's webhook can beat the client's own verify call back to the
    // server (e.g. the guardian's browser is slow or the tab is backgrounded). Same guarantee, other
    // direction — still exactly one FeePayments row for the shared payment id.
    [Fact]
    public async Task Webhook_then_verify_for_same_payment_does_not_duplicate()
    {
        await using var app = App(fx);
        var (tenantId, principalUserId, invoiceId, orderId) = await SeedOrderAsync(app, fx);
        const string paymentId = "pay_CROSSPATH_WV";

        var anonClient = app.CreateClient();
        var webhookRes = await anonClient.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody(orderId, paymentId));
        webhookRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var authedClient = AuthedClient(app, tenantId, principalUserId, "principal");
        var verifyRes = await VerifyAsync(authedClient, invoiceId, orderId, paymentId);
        verifyRes.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(1);
        var status = await conn.QuerySingleAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"FeeInvoices\" WHERE \"Id\" = @invoiceId", new { invoiceId });
        status.Should().Be("paid");
    }

    [Fact]
    public async Task Wrong_secret_webhook_signature_is_rejected_with_no_state_change()
    {
        await using var app = App(fx, signatureValid: false);
        var (tenantId, _, invoiceId, orderId) = await SeedOrderAsync(app, fx);
        var client = app.CreateClient();

        var res = await client.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody(orderId, "pay_BADSIG"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var status = await conn.QuerySingleAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"FeeInvoices\" WHERE \"Id\" = @invoiceId", new { invoiceId });
        status.Should().Be("due");
    }

    [Fact]
    public async Task Unknown_order_id_is_ignored_gracefully()
    {
        await using var app = App(fx);
        var client = app.CreateClient();
        var res = await client.PostAsync("/v1/webhooks/razorpay-fees", WebhookBody("order_does_not_exist", "pay_X"));
        res.StatusCode.Should().Be(HttpStatusCode.OK); // Razorpay should not retry forever on an order we'll never recognize
    }
}
