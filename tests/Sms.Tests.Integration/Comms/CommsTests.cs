using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class CommsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId,
        string[]? roles = null)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, roles ?? [Policies.Teacher], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Thread_send_message_with_is_mine_and_last_message()
    {
        await using var app = App();
        var userId = Guid.NewGuid();
        var client = TenantClient(app, Guid.NewGuid(), userId);

        var thread = await Data(await client.PostAsJsonAsync("/v1/threads",
            new { name = "R. Kumar (Maths)", role = "teacher", group = false }), HttpStatusCode.Created);
        var threadId = thread.GetProperty("id").GetGuid();
        thread.GetProperty("group").GetBoolean().Should().BeFalse();

        var msg = await Data(await client.PostAsJsonAsync($"/v1/threads/{threadId}/messages",
            new { text = "Hello, about the homework..." }), HttpStatusCode.Created);
        msg.GetProperty("is_mine").GetBoolean().Should().BeTrue();
        msg.GetProperty("text").GetString().Should().Be("Hello, about the homework...");

        var messages = await Data(await client.GetAsync($"/v1/threads/{threadId}/messages"), HttpStatusCode.OK);
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("is_mine").GetBoolean().Should().BeTrue();

        var threads = await Data(await client.GetAsync("/v1/threads"), HttpStatusCode.OK);
        threads.EnumerateArray().First(t => t.GetProperty("id").GetGuid() == threadId)
            .GetProperty("last_message").GetString().Should().Be("Hello, about the homework...");
    }

    [Fact]
    public async Task Announcement_create_and_list()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Principal]);

        var created = await Data(await client.PostAsJsonAsync("/v1/announcements",
            new { title = "Sports Day", body = "Friday 9am", type = "event" }), HttpStatusCode.Created);
        created.GetProperty("type").GetString().Should().Be("event");

        var list = await Data(await client.GetAsync("/v1/announcements"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid())
            .Should().Contain(created.GetProperty("id").GetGuid());
    }

    /// The frontend's fee-notification path (Save & generate, and the Send reminders button)
    /// both go through notifyFeeAudience -> POST /v1/announcements with the same shape used
    /// here — this proves a fee notification actually lands in the Communication ›
    /// Announcements list the school sees, not just some fire-and-forget call.
    [Fact]
    public async Task Fee_notification_via_announcements_shows_up_in_the_announcements_list()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid(), Guid.NewGuid(), [Policies.Principal]);

        var created = await Data(await client.PostAsJsonAsync("/v1/announcements", new
        {
            title = "New fee invoices generated",
            body = "Greenwood School: A new fee has been published for Term 1 · 2026-27. Please check the parent app for your dues and pay at the earliest.",
            type = "fee_invoice_created",
            audience = "parents",
            emails = new[] { "parent1@x.com" },
            phones = new[] { "9000000001" },
            channels = new[] { "email", "sms", "app" },
            school_name = "Greenwood School",
            event_kind = "fee_invoice_created",
        }), HttpStatusCode.Created);
        created.GetProperty("type").GetString().Should().Be("fee_invoice_created");
        created.GetProperty("audience").GetString().Should().Be("parents");

        var list = await Data(await client.GetAsync("/v1/announcements"), HttpStatusCode.OK);
        var row = list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
        row.GetProperty("title").GetString().Should().Be("New fee invoices generated");
        row.GetProperty("type").GetString().Should().Be("fee_invoice_created");
    }
}
