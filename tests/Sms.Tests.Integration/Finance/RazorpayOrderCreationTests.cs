using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.Finance;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class RazorpayOrderCreationTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class FakeRazorpayClient : IRazorpayClient
    {
        public Task<RazorpayOrderCreated> CreateOrderAsync(
            string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default) =>
            Task.FromResult(new RazorpayOrderCreated($"order_fake_{amountPaise}", amountPaise, currency));
        public bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature) => true;
        public bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader) => true;
    }

    private static WebApplicationFactory<Program> App(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddSingleton<IRazorpayClient, FakeRazorpayClient>());
        });

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

    private static async Task<(Guid tenantId, Guid principalUserId, Guid studentId, Guid invoiceId)> SeedAsync(
        WebApplicationFactory<Program> app, PostgresFixture fx, decimal invoiceAmount = 5000m)
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade) " +
            "VALUES (@studentId, @tenantId, 'A200', 'Kabir Shah', 'active', '6')",
            new { studentId, tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
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
            "INSERT dbo.TenantPaymentCredentials (TenantId, Provider, KeyId, KeySecretEncrypted, Mode, IsEnabled) " +
            "VALUES (@tenantId, 'razorpay', 'rzp_test_seed', @encryptedSecret, 'test', 1)",
            new { tenantId, encryptedSecret });

        return (tenantId, principalUserId, studentId, invoiceId);
    }

    [Fact]
    public async Task Staff_can_create_an_order_for_the_full_remaining_balance_and_gets_a_pay_link()
    {
        await using var app = App(fx);
        var (tenantId, principalUserId, _, invoiceId) = await SeedAsync(app, fx, invoiceAmount: 4800m);
        var client = AuthedClient(app, tenantId, principalUserId, "principal");

        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("amount").GetInt64().Should().Be(480000); // 4800 rupees -> paise
        data.GetProperty("key_id").GetString().Should().Be("rzp_test_seed");
        data.GetProperty("order_id").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unlinked_parent_cannot_create_an_order_for_another_students_invoice()
    {
        await using var app = App(fx);
        var (tenantId, _, _, invoiceId) = await SeedAsync(app, fx);
        var unlinkedParentUserId = Guid.NewGuid();
        var client = AuthedClient(app, tenantId, unlinkedParentUserId, "parent");

        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Order_creation_fails_when_invoice_is_already_fully_paid()
    {
        await using var app = App(fx);
        var (tenantId, principalUserId, studentId, invoiceId) = await SeedAsync(app, fx, invoiceAmount: 1000m);
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "UPDATE dbo.FeeInvoices SET PaidAmount = 1000, Status = 'paid' WHERE Id = @invoiceId", new { invoiceId });
        var client = AuthedClient(app, tenantId, principalUserId, "principal");

        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Order_creation_fails_when_credentials_are_not_configured()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using (var conn = new SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade) " +
                "VALUES (@studentId, @tenantId, 'A201', 'No Creds', 'active', '6')", new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
                "VALUES (@invoiceId, @tenantId, @studentId, 'Term 1', 1000, 0, 'due')", new { invoiceId, tenantId, studentId });
            // deliberately NOT inserting TenantPaymentCredentials
        }
        var client = AuthedClient(app, tenantId, principalUserId, "principal");

        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
