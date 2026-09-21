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

/// <summary>
/// Task 8: whole-flow acceptance tests closing the gaps left by Tasks 4-7's own test files
/// (each written by an implementer who only saw their own task, per the brief). These three
/// tests exercise spec rules that had no test anywhere on the branch before this file:
/// rule 10 (multi-child parent isolation), the cross-tenant half of rule 11 (an id-guessing
/// attack, not just an unlinked-child-in-the-same-tenant guess), and the tier-not-entitled
/// half of the feature-gate cross-check (Integration test 9 in the spec's §8 matrix).
/// See docs/superpowers/specs/2026-09-09-razorpay-fee-payment-design.md §8.
/// </summary>
[Collection("sql")]
public class RazorpayFeePaymentAcceptanceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class FakeRazorpayClient : IRazorpayClient
    {
        public Task<RazorpayOrderCreated> CreateOrderAsync(
            string keyId, string keySecret, long amountPaise, string currency, string receipt, CancellationToken ct = default) =>
            Task.FromResult(new RazorpayOrderCreated($"order_fake_{Guid.NewGuid():N}", amountPaise, currency));
        public bool VerifyPaymentSignature(string keySecret, string orderId, string paymentId, string signature) => true;
        public bool VerifyWebhookSignature(string webhookSecret, string body, string signatureHeader) => true;
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

    private static (WebApplicationFactory<Program> app, CapturingAnnouncementService announcements) App(PostgresFixture fx)
    {
        var fake = new CapturingAnnouncementService();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IRazorpayClient, FakeRazorpayClient>();
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

    private static async Task SeedCredentialsAsync(WebApplicationFactory<Program> app, NpgsqlConnection conn, Guid tenantId)
    {
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
    }

    private static async Task<string> CreateOrderAsync(HttpClient client, Guid invoiceId)
    {
        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("order_id").GetString()!;
    }

    // Rule 10: multi-child parent — verify child B's order/verify never touches child A's data.
    [Fact]
    public async Task Parent_with_two_children_can_only_act_on_the_child_they_selected()
    {
        var (app, announcements) = App(fx);
        await using var _ = app;

        var tenantId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var studentA = Guid.NewGuid();
        var studentB = Guid.NewGuid();
        var invoiceA = Guid.NewGuid();
        var invoiceB = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@parentUserId, @tenantId, 'Two Kids Parent')",
                new { parentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade, GuardianEmail) " +
                "VALUES (@studentA, @tenantId, 'A500', 'Child A', 'active', '4', 'twokids@school.test')",
                new { studentA, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade, GuardianEmail) " +
                "VALUES (@studentB, @tenantId, 'A501', 'Child B', 'active', '5', 'twokids@school.test')",
                new { studentB, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@parentUserId, @studentA, @tenantId)",
                new { parentUserId, studentA, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@parentUserId, @studentB, @tenantId)",
                new { parentUserId, studentB, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
                "VALUES (@invoiceA, @tenantId, @studentA, 'Term 1', 3000, 0, 'due')", new { invoiceA, tenantId, studentA });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
                "VALUES (@invoiceB, @tenantId, @studentB, 'Term 1', 4000, 0, 'due')", new { invoiceB, tenantId, studentB });
            await SeedCredentialsAsync(app, conn, tenantId);
        }

        var client = AuthedClient(app, tenantId, parentUserId, "parent");
        var orderId = await CreateOrderAsync(client, invoiceB);

        var verify = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceB}/razorpay/verify", new
        {
            razorpay_order_id = orderId, razorpay_payment_id = "pay_CHILD_B", razorpay_signature = "any-value-fake-client-accepts-everything",
        });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await check.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });

        var statusA = await check.QuerySingleAsync<string>("SELECT Status FROM dbo.FeeInvoices WHERE Id = @invoiceA", new { invoiceA });
        var statusB = await check.QuerySingleAsync<string>("SELECT Status FROM dbo.FeeInvoices WHERE Id = @invoiceB", new { invoiceB });
        statusA.Should().Be("due"); // untouched
        statusB.Should().Be("paid");

        var paymentsA = await check.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.FeePayments WHERE InvoiceId = @invoiceA", new { invoiceA });
        var paymentsB = await check.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.FeePayments WHERE InvoiceId = @invoiceB", new { invoiceB });
        paymentsA.Should().Be(0);
        paymentsB.Should().Be(1);

        announcements.Created.Should().ContainSingle();
        announcements.Created[0].Body.Should().Contain("Child B");
        announcements.Created[0].Body.Should().NotContain("Child A");
    }

    // Rule 11 (cross-tenant half not covered elsewhere): a staff/parent token from tenant A can
    // never create or verify an order against tenant B's invoice, even by guessing the invoice id.
    [Fact]
    public async Task Cross_tenant_invoice_id_guess_is_rejected()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddSingleton<IRazorpayClient, FakeRazorpayClient>());
        });

        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var staffAUserId = Guid.NewGuid();
        var studentBId = Guid.NewGuid();
        var invoiceBId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantAId, tier: "platinum");
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantBId, tier: "platinum");
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            // Tenant A: staff user only, no invoice of its own needed for this test.
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantAId", new { tenantAId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@staffAUserId, @tenantAId, 'Staff In Tenant A')",
                new { staffAUserId, tenantAId });

            // Tenant B: the invoice tenant A's staff will try to guess.
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantBId", new { tenantBId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade) " +
                "VALUES (@studentBId, @tenantBId, 'B900', 'Tenant B Kid', 'active', '9')", new { studentBId, tenantBId });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
                "VALUES (@invoiceBId, @tenantBId, @studentBId, 'Term 1', 2500, 0, 'due')", new { invoiceBId, tenantBId, studentBId });
            await SeedCredentialsAsync(app, conn, tenantBId);
        }

        // The caller is authenticated for tenant A but posts against tenant B's invoice id.
        var crossTenantClient = AuthedClient(app, tenantAId, staffAUserId, "principal");

        var orderRes = await crossTenantClient.PostAsync($"/v1/fees/invoices/{invoiceBId}/razorpay/order", null);
        orderRes.StatusCode.Should().Be(HttpStatusCode.NotFound); // tenant-scoped lookup, not a 403 that would confirm the id exists

        var verifyRes = await crossTenantClient.PostAsJsonAsync($"/v1/fees/invoices/{invoiceBId}/razorpay/verify", new
        {
            razorpay_order_id = "order_does_not_matter", razorpay_payment_id = "pay_GUESS", razorpay_signature = "x",
        });
        verifyRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await check.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantBId", new { tenantBId });
        var status = await check.QuerySingleAsync<string>("SELECT Status FROM dbo.FeeInvoices WHERE Id = @invoiceBId", new { invoiceBId });
        status.Should().Be("due");
        var payments = await check.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.FeePayments WHERE InvoiceId = @invoiceBId", new { invoiceBId });
        payments.Should().Be(0);
    }

    // Feature-gate cross-check: a non-platinum tenant is blocked even with valid configured credentials.
    [Fact]
    public async Task Non_entitled_tier_cannot_create_an_order_even_with_credentials_configured()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddSingleton<IRazorpayClient, FakeRazorpayClient>());
        });

        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "silver"); // not platinum -> OnlineFeePayment not entitled
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade) " +
                "VALUES (@studentId, @tenantId, 'S700', 'Silver Tier Kid', 'active', '6')", new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, Amount, PaidAmount, Status) " +
                "VALUES (@invoiceId, @tenantId, @studentId, 'Term 1', 1500, 0, 'due')", new { invoiceId, tenantId, studentId });
            // Credentials ARE configured — the feature-tier gate, not the credentials gate, must be what blocks this.
            await SeedCredentialsAsync(app, conn, tenantId);
        }

        var client = AuthedClient(app, tenantId, principalUserId, "principal");
        var res = await client.PostAsync($"/v1/fees/invoices/{invoiceId}/razorpay/order", null);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("feature_locked");
    }
}
