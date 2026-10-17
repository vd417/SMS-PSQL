using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.Tenancy;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// <summary>
/// Recurring cycle billing (IBillingRunService): when a subscription's renewal date passes, the next
/// cycle invoice is generated and the subscription advances; a re-run does not double-bill.
/// </summary>
[Collection("sql")]
public class RecurringBillingTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.UseSetting("Billing:RecurringEnabled", "false"); // drive the service manually instead
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

    private static async Task<int> RunBillingAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(null, null, true);
        var svc = scope.ServiceProvider.GetRequiredService<IBillingRunService>();
        return await svc.RunDueAsync(DateTime.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task Due_subscription_gets_one_cycle_invoice_and_does_not_double_bill()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var planId = (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "PerStudent-Cycle", tier = "gold", pricing = "per_student",
            price = 0, per_student = 10, min_students = 0, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 100, staff = 50, storage_gb = 20 },
            visibility = "published", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Cycle School", slug = "cycle-school", country = "IN",
            plan_id = planId, trial_days = 0, students_count = 10
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        await Data(await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }),
            HttpStatusCode.OK);

        var before = (await Data(await client.GetAsync($"/v1/invoices?tenant_id={id}"), HttpStatusCode.OK))
            .GetArrayLength();

        // Force the subscription's renewal into the past so it is due now.
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE \"dbo\".\"Subscriptions\" SET \"RenewsAt\" = @past WHERE \"TenantId\" = @id AND \"Status\" = 'active'",
                new { id, past = DateTime.UtcNow.AddDays(-1) });
        }

        // First run issues exactly one cycle invoice (10 students x ₹10 x 1 month = 100).
        (await RunBillingAsync(app)).Should().Be(1);

        var after = await Data(await client.GetAsync($"/v1/invoices?tenant_id={id}"), HttpStatusCode.OK);
        after.GetArrayLength().Should().Be(before + 1);
        after.EnumerateArray().Select(i => i.GetProperty("amount").GetDecimal())
            .Should().Contain(100m);

        // Second run is a no-op — the period already advanced, so no double billing.
        (await RunBillingAsync(app)).Should().Be(0);
    }
}
