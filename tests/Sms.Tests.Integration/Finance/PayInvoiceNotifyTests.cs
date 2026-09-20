using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Common;
using Sms.Application.Services.Comms;
using Sms.Application.Services.Finance;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class PayInvoiceNotifyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingAnnouncementService : IAnnouncementService
    {
        public List<CreateAnnouncementRequest> Created { get; } = [];
        public bool ThrowOnCreate { get; set; }
        public int CallCount { get; private set; }

        public Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AnnouncementResponse>>.Ok(Array.Empty<AnnouncementResponse>()));

        public Task<ApiResult<AnnouncementResponse>> CreateAsync(
            CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default)
        {
            CallCount++;
            if (ThrowOnCreate)
                throw new InvalidOperationException("simulated announcement failure");
            Created.Add(req);
            return Task.FromResult(ApiResult<AnnouncementResponse>.Ok(
                new AnnouncementResponse(Guid.NewGuid(), Guid.Empty, req.Title, req.Body, DateTime.UtcNow, null, role, req.Type ?? "general", false, req.Audience)));
        }
    }

    /// Captures the exact model handed to the PDF generator, so a test can assert on receipt
    /// content (e.g. the payment reference) without needing to parse the actual PDF bytes —
    /// QuestPDF embeds a subset font with its own glyph encoding, so the rendered text is not
    /// searchable in the raw or decompressed PDF stream.
    private sealed class CapturingPdfGenerator : IFeeInvoicePdfGenerator
    {
        public FeeInvoicePdfModel? LastModel { get; private set; }
        public byte[] Generate(FeeInvoicePdfModel model)
        {
            LastModel = model;
            return [1, 2, 3];
        }
    }

    private static WebApplicationFactory<Program> BuildApp(
        PostgresFixture fx, CapturingAnnouncementService fake, CapturingPdfGenerator? pdfFake = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services =>
            {
                services.AddScoped<IAnnouncementService>(_ => fake);
                if (pdfFake is not null)
                    services.AddScoped<IFeeInvoicePdfGenerator>(_ => pdfFake);
            });
        });

    private static async Task<(Guid tenantId, Guid principalUserId, Guid studentId, Guid invoiceId)> SeedInvoiceAsync(
        PostgresFixture fx, string guardianEmail, string guardianPhone, Guid? guardianUserId = null)
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade, GuardianEmail, GuardianPhone) " +
            "VALUES (@studentId, @tenantId, 'A100', 'Aarav Sharma', 'active', '5', @guardianEmail, @guardianPhone)",
            new { studentId, tenantId, guardianEmail, guardianPhone });
        await conn.ExecuteAsync(
            "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, DueDate, Amount, Status) " +
            "VALUES (@invoiceId, @tenantId, @studentId, 'Term 2 2026', '2026-03-15', 8500, 'due')",
            new { invoiceId, tenantId, studentId });
        if (guardianUserId is { } gid)
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name, Email) VALUES (@gid, @tenantId, 'Guardian Of Aarav', @guardianEmail)",
                new { gid, tenantId, guardianEmail });
        }

        return (tenantId, principalUserId, studentId, invoiceId);
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Cash_payment_fires_one_notification_with_the_real_amount_and_guardian_contacts()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var guardianUserId = Guid.NewGuid();
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(
            fx, "guardian-aarav@school.test", "+91-9000000000", guardianUserId);
        var client = Client(app, tenantId, principalUserId);

        var pay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new { amount = 8500, method = "Cash" });
        pay.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle();
        var notify = fake.Created[0];
        notify.Title.Should().Be("Invoice Paid");
        notify.Audience.Should().Be("specific");
        notify.Emails.Should().ContainSingle().Which.Should().Be("guardian-aarav@school.test");
        notify.Phones.Should().ContainSingle().Which.Should().Be("+91-9000000000");
        notify.Channels.Should().BeEquivalentTo(new[] { "email", "app" });
        notify.UserId.Should().Be(guardianUserId);
        notify.Body.Should().Contain("8,500").And.Contain("Cash").And.Contain("Aarav Sharma");
        notify.AttachmentBase64.Should().NotBeNullOrEmpty();
        notify.AttachmentFileName.Should().EndWith(".pdf");
        notify.AttachmentContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task Idempotent_replay_does_not_notify_again()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(fx, "guardian-aarav@school.test", "+91-9000000000");
        var client = Client(app, tenantId, principalUserId);
        var idempotencyKey = Guid.NewGuid();

        var first = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay",
            new { amount = 8500, method = "Cash", idempotency_key = idempotencyKey });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Created.Should().ContainSingle();

        var replay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay",
            new { amount = 8500, method = "Cash", idempotency_key = idempotencyKey });
        replay.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle("a replayed idempotency key must not fire a second notification");
    }

    [Fact]
    public async Task Already_fully_paid_invoice_does_not_notify()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(fx, "guardian-aarav@school.test", "+91-9000000000");
        var client = Client(app, tenantId, principalUserId);

        (await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new { amount = 8500, method = "Cash" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Created.Should().ContainSingle();

        var secondAttempt = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new { amount = 8500, method = "Cash" });
        secondAttempt.StatusCode.Should().Be(HttpStatusCode.Conflict);

        fake.Created.Should().ContainSingle("an already-fully-paid invoice's rejected second payment must not notify");
    }

    [Fact]
    public async Task Notify_service_throwing_still_leaves_the_payment_recorded_and_the_api_call_successful()
    {
        var fake = new CapturingAnnouncementService { ThrowOnCreate = true };
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(fx, "guardian-aarav@school.test", "+91-9000000000");
        var client = Client(app, tenantId, principalUserId);

        var pay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new { amount = 8500, method = "Cash" });

        pay.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CallCount.Should().Be(1, "the notify attempt must actually happen, not be skipped, before failing best-effort");

        var invoices = await client.GetAsync($"/v1/fees/invoices?student_id={await StudentIdForAsync(fx, tenantId)}");
        invoices.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<Guid> StudentIdForAsync(PostgresFixture fx, Guid tenantId)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        return await conn.QuerySingleAsync<Guid>(
            "SELECT TOP 1 Id FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId });
    }

    [Fact]
    public async Task Concurrent_pay_requests_with_the_same_idempotency_key_notify_exactly_once()
    {
        // Regression test for the duplicate-notification race: verify and webhook (or any two callers)
        // racing on the same payment must fire the guardian notification exactly once, not twice. One
        // caller wins the FeePayments insert; the other must land on the idempotency-conflict path
        // (top-of-transaction lookup finding the winner's row, or losing the unique-index race) and be
        // handed back the winner's payment WITHOUT that being treated as "I just created this, notify".
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(
            fx, "guardian-race@school.test", "+91-9000000099");
        var idempotencyKey = Guid.NewGuid();

        var client1 = Client(app, tenantId, principalUserId);
        var client2 = Client(app, tenantId, principalUserId);
        // Deliberately a partial amount (invoice total is 8500): this keeps the invoice "partial"
        // rather than "paid" after the winning insert commits, so the losing concurrent call reaches
        // RecordInvoicePaymentAsync's own INSERT (and its unique-index conflict handling) instead of
        // short-circuiting on the unrelated "invoice already fully paid" check.
        var body = new { amount = 1500, method = "Cash", idempotency_key = idempotencyKey };

        var task1 = client1.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body);
        var task2 = client2.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body);
        var responses = await Task.WhenAll(task1, task2);

        responses[0].StatusCode.Should().Be(HttpStatusCode.OK);
        responses[1].StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle(
            "both concurrent calls for the same payment resolved to one payment row, so exactly one notification must fire");
    }

    [Fact]
    public async Task Paying_student_A_invoice_never_notifies_student_B_guardian()
    {
        var fake = new CapturingAnnouncementService();
        var app = BuildApp(fx, fake);
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentAId = Guid.NewGuid();
        var studentBId = Guid.NewGuid();
        var invoiceAId = Guid.NewGuid();

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade, GuardianEmail, GuardianPhone) VALUES " +
                "(@studentAId, @tenantId, 'A100', 'Aarav Sharma', 'active', '5', 'guardian-a@school.test', '+91-9000000001'), " +
                "(@studentBId, @tenantId, 'B100', 'Bela Iyer', 'active', '5', 'guardian-b@school.test', '+91-9000000002')",
                new { studentAId, studentBId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.FeeInvoices (Id, TenantId, StudentId, Period, DueDate, Amount, Status) " +
                "VALUES (@invoiceAId, @tenantId, @studentAId, 'Term 2 2026', '2026-03-15', 8500, 'due')",
                new { invoiceAId, tenantId, studentAId });
        }

        var client = Client(app, tenantId, principalUserId);
        var pay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceAId}/pay", new { amount = 8500, method = "Cash" });
        pay.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().ContainSingle();
        fake.Created[0].Emails.Should().ContainSingle().Which.Should().Be("guardian-a@school.test");
    }

    [Fact]
    public async Task Razorpay_style_payment_with_a_reference_puts_it_on_the_receipt_model()
    {
        var fake = new CapturingAnnouncementService();
        var pdfFake = new CapturingPdfGenerator();
        var app = BuildApp(fx, fake, pdfFake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(fx, "guardian-aarav@school.test", "+91-9000000000");
        var client = Client(app, tenantId, principalUserId);

        var pay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay",
            new { amount = 8500, method = "Razorpay", @ref = "pay_QWERTY12345" });
        pay.StatusCode.Should().Be(HttpStatusCode.OK);

        pdfFake.LastModel.Should().NotBeNull();
        pdfFake.LastModel!.Ref.Should().Be("pay_QWERTY12345");
    }

    [Fact]
    public async Task Cash_payment_without_a_reference_leaves_the_receipt_model_ref_empty()
    {
        var fake = new CapturingAnnouncementService();
        var pdfFake = new CapturingPdfGenerator();
        var app = BuildApp(fx, fake, pdfFake);
        var (tenantId, principalUserId, _, invoiceId) = await SeedInvoiceAsync(fx, "guardian-aarav@school.test", "+91-9000000000");
        var client = Client(app, tenantId, principalUserId);

        var pay = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new { amount = 8500, method = "Cash" });
        pay.StatusCode.Should().Be(HttpStatusCode.OK);

        pdfFake.LastModel.Should().NotBeNull();
        pdfFake.LastModel!.Ref.Should().BeNullOrEmpty(
            "a cash/manual payment with no reference must not carry a placeholder ref onto the receipt");
    }
}
