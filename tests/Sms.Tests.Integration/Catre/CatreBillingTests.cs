using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

[Collection("sql")]
public class CatreBillingTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PlatformClient(WebApplicationFactory<Program> app)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), null, ["owner"], isPlatform: true);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, string name, string tier, decimal price)
    {
        var res = await client.PostAsJsonAsync("/v1/plans", new
        {
            name, tier, pricing = "flat", price, period = "month",
            limits = new { students = 1000, staff = 100, storage_gb = 40 },
            visibility = "published", audience = "all"
        });
        return (await Data(res, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Invoice_mark_paid_then_refund_and_refund_conflict_on_open()
    {
        var factory = PlatformFactory();
        Guid invoiceId;
        await using (var c = await factory.OpenAsync())
            invoiceId = await c.QuerySingleAsync<Guid>(
                "INSERT INTO \"dbo\".\"Invoices\" (\"Id\", \"TenantId\", \"TenantName\", \"PlanName\", \"Amount\", \"Status\", \"Issued\", \"Due\") " +
                "VALUES (gen_random_uuid(), @t, 'Greenwood', 'Gold', 14999, 'open', now(), now()) RETURNING \"Id\"",
                new { t = Guid.NewGuid() });

        await using var app = App();
        var client = PlatformClient(app);

        // refund while open -> 409
        (await client.PostAsync($"/v1/invoices/{invoiceId}/refund", null)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        // mark paid
        var paid = await Data(await client.PostAsync($"/v1/invoices/{invoiceId}/mark-paid", null), HttpStatusCode.OK);
        paid.GetProperty("status").GetString().Should().Be("paid");
        paid.GetProperty("paid_on").ValueKind.Should().NotBe(JsonValueKind.Null);

        // refund
        var refunded = await Data(await client.PostAsync($"/v1/invoices/{invoiceId}/refund", null), HttpStatusCode.OK);
        refunded.GetProperty("status").GetString().Should().Be("open");

        // list + get
        (await Data(await client.GetAsync($"/v1/invoices/{invoiceId}"), HttpStatusCode.OK))
            .GetProperty("amount").GetDecimal().Should().Be(14999);
    }

    [Fact]
    public async Task Subscription_create_and_get()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var plan = await CreatePlanAsync(client, "Gold", "gold", 14999);
        var tenant = Guid.NewGuid();

        var created = await Data(await client.PostAsJsonAsync("/v1/subscriptions",
            new { tenant_id = tenant, plan_id = plan, seats = 1200 }), HttpStatusCode.Created);
        created.GetProperty("status").GetString().Should().Be("active");
        created.GetProperty("seats").GetInt32().Should().Be(1200);

        var id = created.GetProperty("id").GetGuid();
        (await Data(await client.GetAsync($"/v1/subscriptions/{id}"), HttpStatusCode.OK))
            .GetProperty("plan_id").GetGuid().Should().Be(plan);
    }

    [Fact]
    public async Task Dashboard_overview_reflects_clients_and_mrr()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        // two trial clients; activate one
        async Task<Guid> NewClient() => (await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "School", slug = $"s-{Guid.NewGuid():N}", plan_id = gold, trial_days = 14
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var a = await NewClient();
        await NewClient();
        await client.PostAsJsonAsync($"/v1/clients/{a}/status", new { status = "active" });

        var d = await Data(await client.GetAsync("/v1/dashboard/overview"), HttpStatusCode.OK);
        d.GetProperty("counts").GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        d.GetProperty("counts").GetProperty("active").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        d.GetProperty("counts").GetProperty("trial").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        d.GetProperty("mrr").GetDecimal().Should().BeGreaterThanOrEqualTo(14999);
        d.GetProperty("plan_mix").EnumerateArray().Select(e => e.GetProperty("label").GetString())
            .Should().Contain("gold");
        d.GetProperty("system_health").GetArrayLength().Should().BeGreaterThan(0);
    }
}
