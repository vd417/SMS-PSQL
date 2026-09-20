using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class SchoolIntegrationsControllerTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

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

    private static WebApplicationFactory<Program> App(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task Owner_can_configure_razorpay_and_secret_is_never_echoed_back()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.owner");

        var put = await client.PutAsJsonAsync("/v1/school/integrations", new
        {
            razorpay = new { key_id = "rzp_test_owner", key_secret = "sekrit", webhook_secret = "whsekrit", mode = "test", enabled = true },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync("/v1/school/integrations");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var razorpay = doc.RootElement.GetProperty("data").GetProperty("razorpay");
        razorpay.GetProperty("key_id").GetString().Should().Be("rzp_test_owner");
        razorpay.GetProperty("key_secret_set").GetBoolean().Should().BeTrue();
        razorpay.TryGetProperty("key_secret", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Partial_body_put_preserves_existing_mode_and_enabled()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.owner");

        await client.PutAsJsonAsync("/v1/school/integrations", new
        {
            razorpay = new { key_id = "rzp_test_initial", key_secret = "sekrit", webhook_secret = "whsekrit", mode = "live", enabled = true },
        });

        // Partial body: only key_id given, mode/enabled omitted entirely — must not silently reset
        // mode back to "test" or disable the integration.
        var put = await client.PutAsJsonAsync("/v1/school/integrations", new
        {
            razorpay = new { key_id = "rzp_test_updated" },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync("/v1/school/integrations");
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var razorpay = doc.RootElement.GetProperty("data").GetProperty("razorpay");
        razorpay.GetProperty("key_id").GetString().Should().Be("rzp_test_updated");
        razorpay.GetProperty("mode").GetString().Should().Be("live");
        razorpay.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Principal_is_forbidden_from_configuring_razorpay()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "principal");

        var res = await client.PutAsJsonAsync("/v1/school/integrations", new { razorpay = new { key_id = "x" } });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Verify_action_reports_configured_status()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.owner");
        await client.PutAsJsonAsync("/v1/school/integrations", new
        {
            razorpay = new { key_id = "rzp_test_v", key_secret = "s", webhook_secret = "w", mode = "test", enabled = true },
        });

        var res = await client.PostAsync("/v1/school/integrations/razorpay/verify", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().BeOneOf("configured", "invalid");
    }
}
