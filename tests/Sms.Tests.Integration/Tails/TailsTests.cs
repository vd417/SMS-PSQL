using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Tails;

[Collection("sql")]
public class TailsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, Guid.NewGuid(), ["admin"], isPlatform: false);
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
    public async Task Complaint_create_and_resolve()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var created = await Data(await client.PostAsJsonAsync("/v1/complaints", new
        {
            subject = "Bus late", from = "Parent", category = "Transport", priority = "high", body = "Route 7 delayed"
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("open");
        created.GetProperty("from").GetString().Should().Be("Parent");

        var resolved = await Data(await client.PatchAsJsonAsync($"/v1/complaints/{id}",
            new { status = "resolved", assignee = "Ops" }), HttpStatusCode.OK);
        resolved.GetProperty("status").GetString().Should().Be("resolved");
        resolved.GetProperty("assignee").GetString().Should().Be("Ops");
    }

    [Fact]
    public async Task Notification_and_payslip_create_and_list()
    {
        await using var app = App();
        var userId = Guid.NewGuid();
        var client = TenantClient(app, userId);

        var note = await Data(await client.PostAsJsonAsync("/v1/notifications",
            new { icon = "bell", tone = "info", title = "Fees due", body = "Term 4 fees are due" }), HttpStatusCode.Created);
        note.GetProperty("unread").GetBoolean().Should().BeTrue();
        (await Data(await client.GetAsync("/v1/notifications"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(note.GetProperty("id").GetGuid());

        var slip = await Data(await client.PostAsJsonAsync("/v1/payslips",
            new { user_id = userId, month = "April", year = 2026, gross = 60000, deductions = 8000, net = 52000 }),
            HttpStatusCode.Created);
        slip.GetProperty("net").GetDecimal().Should().Be(52000);
        slip.GetProperty("status").GetString().Should().Be("pending");

        (await Data(await client.GetAsync("/v1/payslips"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(slip.GetProperty("id").GetGuid());
    }
}
