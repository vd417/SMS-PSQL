using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class UserAppSettingsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Get_returns_all_on_defaults_then_patch_persists()
    {
        await using var app = App();
        var student = Client(app, Guid.NewGuid(), Policies.StudentOrParent);

        var first = await Data(await student.GetAsync("/v1/me/settings"), HttpStatusCode.OK);
        first.GetProperty("chat_alerts").GetBoolean().Should().BeTrue();
        first.GetProperty("school_notices").GetBoolean().Should().BeTrue();
        first.GetProperty("in_app_toasts").GetBoolean().Should().BeTrue();

        var patched = await Data(await student.PatchAsJsonAsync("/v1/me/settings", new
        {
            chat_alerts = false,
            in_app_toasts = true,
        }), HttpStatusCode.OK);
        patched.GetProperty("chat_alerts").GetBoolean().Should().BeFalse();
        patched.GetProperty("school_notices").GetBoolean().Should().BeTrue();
        patched.GetProperty("in_app_toasts").GetBoolean().Should().BeTrue();

        var again = await Data(await student.GetAsync("/v1/me/settings"), HttpStatusCode.OK);
        again.GetProperty("chat_alerts").GetBoolean().Should().BeFalse();
    }
}
