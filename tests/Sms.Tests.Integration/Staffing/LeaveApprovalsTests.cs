using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class LeaveApprovalsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [Policies.Principal], isPlatform: false);
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
    public async Task Leave_file_appears_in_approvals_then_approve_clears_it()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = TenantClient(app, tenantId, Guid.NewGuid());

        var filed = await Data(await client.PostAsJsonAsync("/v1/leave", new
        {
            type = "casual", from_date = "2026-05-20", to_date = "2026-05-21", reason = "Personal work"
        }), HttpStatusCode.Created);
        var id = filed.GetProperty("id").GetGuid();
        filed.GetProperty("status").GetString().Should().Be("pending");

        // appears in my leave
        (await Data(await client.GetAsync("/v1/leave"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        // appears in pending approvals inbox
        (await Data(await client.GetAsync("/v1/approvals"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        // approve
        var decided = await Data(await client.PatchAsJsonAsync($"/v1/approvals/{id}",
            new { status = "approved", decided_note = "Approved — covered" }), HttpStatusCode.OK);
        decided.GetProperty("status").GetString().Should().Be("approved");
        decided.GetProperty("decided_note").GetString().Should().Be("Approved — covered");

        // no longer in pending inbox
        (await Data(await client.GetAsync("/v1/approvals"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().NotContain(id);

        // stays in CRM Approved tab
        (await Data(await client.GetAsync("/v1/approvals?status=approved"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);
    }
}
