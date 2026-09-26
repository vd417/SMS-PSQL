using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Payments;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// Approving a plan payment (POST /v1/upgrade-requests/{id}/approve) must email the school contact
/// through the existing email queue: the activation invoice email when a trial school becomes
/// active (Razorpay or offline payment), the paid-invoice email for an already-active school's
/// upgrade, exactly once per approval. The email queue is captured, so assertions are on what
/// the app hands to delivery, independent of SMTP.
[Collection("sql")]
public class PlanUpgradeApprovalEmailTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingQueue : IEmailQueue
    {
        private readonly List<EmailMessage> _items = [];
        public IReadOnlyList<EmailMessage> Items { get { lock (_items) return _items.ToList(); } }
        public void Enqueue(EmailMessage message) { lock (_items) _items.Add(message); }
        // EmailDispatchWorker calls this on host start; block until shutdown.
        public async ValueTask<EmailMessage> DequeueAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }

    /// Accepts any checkout signature, standing in for Razorpay's HMAC check (not under test here).
    private sealed class AcceptingRazorpay : IRazorpayGateway
    {
        public bool IsConfigured => true;
        public string KeyId => "rzp_test_fake";
        public Task<RazorpayOrderCreated> CreateOrderAsync(long amountPaise, string currency, string receipt, CancellationToken ct = default) =>
            throw new NotSupportedException("tests confirm payment without creating an order");
        public bool VerifyPaymentSignature(string orderId, string paymentId, string signature) => true;
        public bool VerifyWebhookSignature(string body, string signatureHeader) => false;
    }

    private sealed class FailingPdf : Sms.Application.Services.Tenancy.IInvoicePdfGenerator
    {
        public byte[] Generate(Sms.Application.Services.Tenancy.InvoicePdfModel model) =>
            throw new InvalidOperationException("pdf renderer down");
    }

    private WebApplicationFactory<Program> App(CapturingQueue queue, bool failingPdf = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailQueue>(queue);
                services.AddSingleton<IRazorpayGateway, AcceptingRazorpay>();
                if (failingPdf)
                    services.AddSingleton<Sms.Application.Services.Tenancy.IInvoicePdfGenerator, FailingPdf>();
            });
        });

    private static HttpClient PlatformClient(WebApplicationFactory<Program> app)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", jwt.IssueAccess(Guid.NewGuid(), null, ["owner"], isPlatform: true));
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> PlanAsync(HttpClient client, string name, string tier, decimal price) =>
        (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name, tier, pricing = "flat", price, period = "month",
            features = new[] { "sis.students" }, limits = new { students = 1200, staff = 120, storage_gb = 50 },
            visibility = "published", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<(Guid Id, string AdminEmail)> TrialClientAsync(HttpClient client, Guid planId, string name)
    {
        var adminEmail = $"owner-{Guid.NewGuid():N}@school.test";
        var id = (await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name, slug = $"upgrade-mail-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "Asha Rao", admin_email = adminEmail, plan_id = planId, trial_days = 14
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return (id, adminEmail);
    }

    private static async Task<JsonElement> RequestPaymentAsync(HttpClient client, Guid tenantId, Guid planId, string mode) =>
        await Data(await client.PostAsJsonAsync($"/v1/clients/{tenantId}/plan-payments", new { plan_id = planId, mode }),
            HttpStatusCode.Created);

    private static async Task ConfirmRazorpayAsync(HttpClient client, Guid requestId) =>
        (await Data(await client.PostAsJsonAsync($"/v1/upgrade-requests/{requestId}/confirm-payment", new
        {
            razorpay_order_id = "order_test_1", razorpay_payment_id = "pay_test_1", razorpay_signature = "sig"
        }), HttpStatusCode.OK)).GetProperty("status").GetString().Should().Be("paid_pending_approval");

    private async Task<(string ClientStatus, string? InvoiceStatus)> StateAsync(Guid tenantId, Guid invoiceId)
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        await using var conn = await new NpgsqlConnectionFactory(fx.ConnectionString, ctx).OpenAsync();
        var status = await conn.ExecuteScalarAsync<string>("""SELECT "Status" FROM "dbo"."Tenants" WHERE "Id" = @tenantId""", new { tenantId });
        var inv = await conn.ExecuteScalarAsync<string?>("""SELECT "Status" FROM "dbo"."Invoices" WHERE "Id" = @invoiceId""", new { invoiceId });
        return (status!, inv);
    }

    private static string BodyOf(EmailMessage m) => m.Body;

    private async Task Trial_school_approval_emails_the_activation_invoice(string mode)
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var silver = await PlanAsync(client, "Silver", "silver", 4999m);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, adminEmail) = await TrialClientAsync(client, silver, $"Trial {mode} High");

        var request = await RequestPaymentAsync(client, tenantId, gold, mode);
        var requestId = request.GetProperty("id").GetGuid();
        var invoiceId = request.GetProperty("invoice_id").GetGuid();
        if (mode == "online") await ConfirmRazorpayAsync(client, requestId);
        var before = queue.Items.Count;

        (await Data(await client.PostAsync($"/v1/upgrade-requests/{requestId}/approve", null), HttpStatusCode.OK))
            .GetProperty("status").GetString().Should().Be("approved");

        var sent = queue.Items.Skip(before).ToList();
        sent.Should().ContainSingle("approval must queue exactly one email to the school contact");
        var mail = sent[0];
        mail.To.Should().Be(adminEmail);
        mail.Subject.Should().StartWith("Invoice for ").And.Contain("₹14999.00");
        BodyOf(mail).Should().Contain("is now an active Catre client on the Gold plan")
            .And.Contain("Invoice amount: ₹14999.00").And.Contain("Status: paid");
        mail.AttachmentContentType.Should().Be("application/pdf");
        mail.AttachmentFileName.Should().Be($"Catre-Invoice-{invoiceId:N}.pdf");
        mail.AttachmentBytes.Should().NotBeNullOrEmpty();

        (await StateAsync(tenantId, invoiceId)).Should().Be(("active", "paid"));
    }

    [Fact]
    public Task Razorpay_approval_of_a_trial_school_emails_the_activation_invoice() =>
        Trial_school_approval_emails_the_activation_invoice("online");

    [Fact]
    public Task Offline_approval_of_a_trial_school_emails_the_activation_invoice() =>
        Trial_school_approval_emails_the_activation_invoice("offline");

    [Fact]
    public async Task Paid_upgrade_of_an_active_school_emails_the_paid_invoice()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var silver = await PlanAsync(client, "Silver", "silver", 4999m);
        var platinum = await PlanAsync(client, "Platinum", "platinum", 29999m);
        var (tenantId, adminEmail) = await TrialClientAsync(client, silver, "Active Upgrade High");
        (await client.PostAsJsonAsync($"/v1/clients/{tenantId}/status", new { status = "active" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var request = await RequestPaymentAsync(client, tenantId, platinum, "online");
        var requestId = request.GetProperty("id").GetGuid();
        var invoiceId = request.GetProperty("invoice_id").GetGuid();
        await ConfirmRazorpayAsync(client, requestId);
        var before = queue.Items.Count;

        (await client.PostAsync($"/v1/upgrade-requests/{requestId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = queue.Items.Skip(before).ToList();
        sent.Should().ContainSingle("an approved paid upgrade must email the paid invoice exactly once");
        var mail = sent[0];
        mail.To.Should().Be(adminEmail);
        mail.Subject.Should().Be("Catre Invoice — Active Upgrade High — ₹29999.00");
        BodyOf(mail).Should().Contain("Plan: Platinum").And.Contain("Amount: ₹29999.00").And.Contain("Status: paid");
        mail.AttachmentFileName.Should().Be($"Catre-Invoice-{invoiceId:N}.pdf");

        (await StateAsync(tenantId, invoiceId)).Should().Be(("active", "paid"));
    }

    [Fact]
    public async Task Approving_the_same_request_again_is_refused_and_sends_nothing()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, _) = await TrialClientAsync(client, gold, "Duplicate Approve High");
        var requestId = (await RequestPaymentAsync(client, tenantId, gold, "offline")).GetProperty("id").GetGuid();

        (await client.PostAsync($"/v1/upgrade-requests/{requestId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterFirst = queue.Items.Count;

        (await client.PostAsync($"/v1/upgrade-requests/{requestId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        queue.Items.Count.Should().Be(afterFirst);
    }

    [Fact]
    public async Task Concurrent_approvals_of_one_request_send_exactly_one_email()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, adminEmail) = await TrialClientAsync(client, gold, "Concurrent Approve High");
        var requestId = (await RequestPaymentAsync(client, tenantId, gold, "offline")).GetProperty("id").GetGuid();
        var before = queue.Items.Count;

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsync($"/v1/upgrade-requests/{requestId}/approve", null)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(3);
        queue.Items.Skip(before).Should().ContainSingle(m => m.To == adminEmail && m.Subject.StartsWith("Invoice for "),
            "the single winning approval activates the trial school, so it sends the activation email");
    }

    [Fact]
    public async Task An_email_failure_does_not_fail_the_committed_approval()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue, failingPdf: true);
        var client = PlatformClient(app);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, _) = await TrialClientAsync(client, gold, "Mail Failure High");
        var request = await RequestPaymentAsync(client, tenantId, gold, "offline");
        var before = queue.Items.Count;

        (await Data(await client.PostAsync($"/v1/upgrade-requests/{request.GetProperty("id").GetGuid()}/approve", null), HttpStatusCode.OK))
            .GetProperty("status").GetString().Should().Be("approved");

        queue.Items.Count.Should().Be(before);
        (await StateAsync(tenantId, request.GetProperty("invoice_id").GetGuid())).Should().Be(("active", "paid"));
    }

    [Fact]
    public async Task Rejecting_a_request_sends_no_email()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, _) = await TrialClientAsync(client, gold, "Rejected High");
        var requestId = (await RequestPaymentAsync(client, tenantId, gold, "offline")).GetProperty("id").GetGuid();
        var before = queue.Items.Count;

        (await client.PostAsync($"/v1/upgrade-requests/{requestId}/reject",
            new StringContent("{\"notes\":\"no\"}", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.OK);

        queue.Items.Count.Should().Be(before);
    }

    [Fact]
    public async Task Change_plan_still_changes_the_plan_without_emailing()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var silver = await PlanAsync(client, "Silver", "silver", 4999m);
        var gold = await PlanAsync(client, "Gold", "gold", 14999m);
        var (tenantId, _) = await TrialClientAsync(client, silver, "Change Plan High");
        (await client.PostAsJsonAsync($"/v1/clients/{tenantId}/status", new { status = "active" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var before = queue.Items.Count;

        var changed = await Data(await client.PostAsJsonAsync($"/v1/clients/{tenantId}/change-plan", new { plan_id = gold }), HttpStatusCode.OK);

        changed.GetProperty("plan_name").GetString().Should().Be("Gold");
        changed.GetProperty("status").GetString().Should().Be("active");
        queue.Items.Count.Should().Be(before, "change-plan has never emailed the school");
    }
}
