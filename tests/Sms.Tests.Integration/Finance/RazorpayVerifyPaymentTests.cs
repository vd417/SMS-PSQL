using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Common;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class RazorpayVerifyPaymentTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class FakeRazorpayClient(bool signatureValid) : IRazorpayClient
    {
        public Task<RazorpayOrderCreated> CreateOrderAsync(
            string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default) =>
            Task.FromResult(new RazorpayOrderCreated($"order_fake_{Guid.NewGuid():N}", amountPaise, currency));
        public bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature) => signatureValid;
        public bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader) => signatureValid;
    }

    private sealed class CapturingAnnouncementService : IAnnouncementService
    {
        public List<CreateAnnouncementRequest> Created { get; } = [];

        public Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AnnouncementResponse>>.Ok(Array.Empty<AnnouncementResponse>()));

        public Task<ApiResult<AnnouncementResponse>> CreateAsync(
            CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default)
        {
            Created.Add(req);
            return Task.FromResult(ApiResult<AnnouncementResponse>.Ok(
                new AnnouncementResponse(Guid.NewGuid(), Guid.Empty, req.Title, req.Body, DateTime.UtcNow, null, role, req.Type ?? "general", false, req.Audience)));
        }
    }

    private static (WebApplicationFactory<Program> app, CapturingAnnouncementService announcements) App(
        PostgresFixture fx, bool signatureValid = true)
    {
        var fake = new CapturingAnnouncementService();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRazorpayClient>(new FakeRazorpayClient(signatureValid));
                services.AddScoped<IAnnouncementService>(_ => fake);
            });
        });
        return (app, fake);
    }

    private static HttpClient AuthedClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<(Guid tenantId, Guid principalUserId, Guid invoiceId, Guid studentId)> SeedAsync(
        WebApplicationFactory<Program> app, PostgresFixture fx, decimal invoiceAmount = 4800m, string guardianEmail = "guardian@school.test")
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Status\", \"Grade\", \"GuardianEmail\") " +
            "VALUES (@studentId, @tenantId, 'A300', 'Meera Rao', 'active', '7', @guardianEmail)",
            new { studentId, tenantId, guardianEmail });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"FeeInvoices\" (\"Id\", \"TenantId\", \"StudentId\", \"Period\", \"Amount\", \"PaidAmount\", \"Status\") " +
            "VALUES (@invoiceId, @tenantId, @studentId, 'Term 1', @invoiceAmount, 0, 'due')",
            new { invoiceId, tenantId, studentId, invoiceAmount });
        // KeySecretEncrypted must be produced via the app's own IDataProtectionProvider under the
        // same purpose string TenantPaymentCredentialService uses ("TenantPaymentCredentials.Razorpay.v1"),
        // since GetActiveAsync unconditionally Unprotect()s it — a raw/plaintext value throws CryptographicException.
        using var scope = app.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("TenantPaymentCredentials.Razorpay.v1");
        var encryptedSecret = protector.Protect("irrelevant-for-this-test-fake-client");
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"TenantPaymentCredentials\" (\"TenantId\", \"Provider\", \"KeyId\", \"KeySecretEncrypted\", \"Mode\", \"IsEnabled\") " +
            "VALUES (@tenantId, 'razorpay', 'rzp_test_seed', @encryptedSecret, 'test', true)",
            new { tenantId, encryptedSecret });
        return (tenantId, principalUserId, invoiceId, studentId);
    }

    private static async Task<string> CreateOrderAsync(HttpClient client, Guid invoiceId)
    {
        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("order_id").GetString()!;
    }

    [Fact]
    public async Task Verify_records_payment_marks_invoice_paid_and_notifies_guardian_exactly_once()
    {
        var (app, announcements) = App(fx);
        await using var _ = app;
        var (tenantId, principalUserId, invoiceId, _) = await SeedAsync(app, fx);
        var client = AuthedClient(app, tenantId, principalUserId, "principal");
        var orderId = await CreateOrderAsync(client, invoiceId);

        var res = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/razorpay/verify", new
        {
            razorpay_order_id = orderId, razorpay_payment_id = "pay_ABC123", razorpay_signature = "any-value-fake-client-accepts-everything",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("method").GetString().Should().Be("Razorpay");

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var status = await conn.QuerySingleAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"FeeInvoices\" WHERE \"Id\" = @invoiceId", new { invoiceId });
        status.Should().Be("paid");
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(1);

        announcements.Created.Should().ContainSingle(); // the existing Phase 1 guardian notification, reused unchanged for Razorpay
    }

    [Fact]
    public async Task Duplicate_verify_with_the_same_payment_id_does_not_create_a_second_payment_or_a_second_notification()
    {
        var (app, announcements) = App(fx);
        await using var _ = app;
        var (tenantId, principalUserId, invoiceId, _) = await SeedAsync(app, fx);
        var client = AuthedClient(app, tenantId, principalUserId, "principal");
        var orderId = await CreateOrderAsync(client, invoiceId);
        var body = new { razorpay_order_id = orderId, razorpay_payment_id = "pay_DUPLICATE", razorpay_signature = "x" };

        var first = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/razorpay/verify", body);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/razorpay/verify", body);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(1);
        announcements.Created.Should().ContainSingle(); // the replay must not notify a second time
    }

    [Fact]
    public async Task Tampered_signature_is_rejected_and_no_payment_is_recorded()
    {
        var (app, _) = App(fx, signatureValid: false);
        await using var __ = app;
        var (tenantId, principalUserId, invoiceId, _) = await SeedAsync(app, fx);
        var client = AuthedClient(app, tenantId, principalUserId, "principal");
        var orderId = await CreateOrderAsync(client, invoiceId);

        var res = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/razorpay/verify", new
        {
            razorpay_order_id = orderId, razorpay_payment_id = "pay_TAMPERED", razorpay_signature = "bad",
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var paymentCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM \"dbo\".\"FeePayments\" WHERE \"InvoiceId\" = @invoiceId", new { invoiceId });
        paymentCount.Should().Be(0);
    }
}
