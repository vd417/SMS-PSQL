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

/// Reinstating a suspended Catre client (POST /v1/clients/{id}/status "active" when it was
/// "suspended") must email the school's contact that access is restored. Reinstate shares the
/// "active" status with first-time activation, so the notice turns on the PRIOR status, not the
/// target one. Captures the email queue to assert on what the app hands to delivery.
[Collection("sql")]
public class ClientReinstatementEmailTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingQueue : IEmailQueue
    {
        public List<EmailMessage> Items { get; } = [];
        public void Enqueue(EmailMessage message) { lock (Items) Items.Add(message); }
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

    private static async Task Status(HttpClient client, Guid id, object body) =>
        (await client.PostAsJsonAsync($"/v1/clients/{id}/status", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Reinstating_a_suspended_client_emails_the_contact_that_access_is_restored()
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
            name = "Reinstate Mail High", slug = $"reinstate-mail-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "Asha Rao", admin_email = adminEmail, plan_id = planId, trial_days = 14
        }), HttpStatusCode.Created);

        await Status(client, id, new { status = "active" });                              // first activation
        await Status(client, id, new { status = "suspended", reason = "Non-payment" });    // suspend
        var before = queue.Items.Count;

        await Status(client, id, new { status = "active" });                              // reinstate

        var sent = queue.Items.Skip(before).ToList();
        sent.Should().ContainSingle(m =>
                m.To == adminEmail
                && m.Subject.Contains("reinstat", StringComparison.OrdinalIgnoreCase),
            $"reinstating should email the contact that access is restored; queued after reinstate: [{string.Join("; ", sent.Select(m => $"{m.To}: {m.Subject}"))}]");
    }
}
