using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// <summary>
/// CATRE team raising a school's billed seats (POST /v1/clients/{id}/seats): the cap and MRR follow
/// the new per-student count, and a mid-cycle proration invoice is issued.
/// </summary>
[Collection("sql")]
public class SeatIncreaseTests(PostgresFixture fx)
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

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Increase_seats_raises_cap_mrr_and_issues_proration()
    {
        await using var app = App();
        var client = PlatformClient(app);

        // Per-student plan: ₹10 / student / month, 10-seat capacity.
        var planRes = await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "PerStudent-10", tier = "gold", pricing = "per_student",
            price = 0, per_student = 10, min_students = 0, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 10, staff = 50, storage_gb = 20 },
            visibility = "published", audience = "all"
        });
        var planId = (await Data(planRes, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Onboard a school committed to 10 students, then activate it (creates sub + invoice + MRR).
        var createRes = await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Growing School", slug = "growing-school", country = "IN",
            plan_id = planId, trial_days = 0, students_count = 10
        });
        var created = await Data(createRes, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(10);

        await Data(await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }),
            HttpStatusCode.OK);

        var invoicesBefore = (await Data(await client.GetAsync($"/v1/invoices?tenant_id={id}"), HttpStatusCode.OK))
            .GetArrayLength();

        // CATRE raises seats 10 -> 20.
        var updated = await Data(
            await client.PostAsJsonAsync($"/v1/clients/{id}/seats", new { seat_limit = 20 }),
            HttpStatusCode.OK);

        updated.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(20);
        updated.GetProperty("mrr").GetDecimal().Should().Be(200m); // 10/student x 20

        // A proration invoice was added for the 10 extra seats (~10 x 10 for a fresh monthly period).
        var invoices = await Data(await client.GetAsync($"/v1/invoices?tenant_id={id}"), HttpStatusCode.OK);
        invoices.GetArrayLength().Should().BeGreaterThan(invoicesBefore);
        invoices.EnumerateArray().Select(i => i.GetProperty("amount").GetDecimal())
            .Should().Contain(a => a > 0m && a <= 100m);
    }

    [Fact]
    public async Task Increase_seats_rejects_a_lower_limit()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var planRes = await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Flat-A", tier = "silver", pricing = "flat", price = 2500, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 50, staff = 20, storage_gb = 10 },
            visibility = "published", audience = "all"
        });
        var planId = (await Data(planRes, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Flat School", slug = "flat-school", country = "IN", plan_id = planId, trial_days = 0
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        // Current cap is 50; asking for 40 is not an increase.
        var res = await client.PostAsJsonAsync($"/v1/clients/{id}/seats", new { seat_limit = 40 });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
