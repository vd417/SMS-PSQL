using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// Activating a Catre client (POST /v1/clients/{id}/status "active") must email the new invoice
/// to the school's contact (the admin email given at creation). Captures the email queue so the
/// assertion is on what the app hands to delivery, independent of SMTP.
[Collection("sql")]
public class ClientActivationEmailTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingQueue : IEmailQueue
    {
        public List<EmailMessage> Items { get; } = [];
        public void Enqueue(EmailMessage message) { lock (Items) Items.Add(message); }
        // EmailDispatchWorker calls this on host start; block until shutdown.
        public async ValueTask<EmailMessage> DequeueAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }

    private WebApplicationFactory<Program> App(CapturingQueue queue) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddSingleton<IEmailQueue>(queue));
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

    private static async Task<Guid> IdOf(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Activating_a_client_emails_the_invoice_to_the_admin_contact()
    {
        var queue = new CapturingQueue();
        await using var app = App(queue);
        var client = PlatformClient(app);
        var adminEmail = $"owner-{Guid.NewGuid():N}@school.test";

        var planId = await IdOf(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Gold", tier = "gold", pricing = "flat", price = 14999m, period = "month",
            features = new[] { "sis.students" }, limits = new { students = 1200, staff = 120, storage_gb = 50 },
            visibility = "published", audience = "all"
        }), HttpStatusCode.Created);
        var id = await IdOf(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Activation Mail High", slug = $"activation-mail-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "Asha Rao", admin_email = adminEmail, plan_id = planId, trial_days = 14
        }), HttpStatusCode.Created);
        var beforeActivation = queue.Items.Count;

        (await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = queue.Items.Skip(beforeActivation).ToList();
        sent.Should().ContainSingle(m => m.To == adminEmail && m.Subject.StartsWith("Invoice for"),
            $"activation should email the invoice to the admin contact; queued after activation: [{string.Join("; ", sent.Select(m => $"{m.To}: {m.Subject}"))}]");
    }
}
