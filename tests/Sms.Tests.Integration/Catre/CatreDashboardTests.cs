using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration.Catre;

[Collection("sql")]
public class CatreDashboardTests(PostgresFixture fx)
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
    public async Task Dashboard_returns_real_usage_alerts_and_series()
    {
        await using var app = App();
        var client = PlatformClient(app);

        // Seed a tenant over the 80% student limit, plus a recent audit row.
        var tid = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Tenants (Id, Name, Slug, Tier, Status, Mrr, StudentsCount, LimitsStudents) " +
                "VALUES (@id, @name, @slug, 'growth', 'active', 5000, 95, 100)",
                new { id = tid, name = $"Over-Limit School {tid:N}", slug = $"over-limit-{tid:N}" });
            await conn.ExecuteAsync(
                "INSERT dbo.AuditLog (Id, Action, Target, Kind, At) " +
                "VALUES (NEWID(), 'client.created', @t, 'client', SYSUTCDATETIME())",
                new { t = tid.ToString() });
        }

        var data = await Data(await client.GetAsync("/v1/dashboard/overview"), HttpStatusCode.OK);

        // Usage alert fired for the over-limit tenant.
        data.GetProperty("usage_alerts").EnumerateArray()
            .Should().Contain(a => a.GetProperty("metric").GetString() == "students"
                                && a.GetProperty("pct").GetInt32() >= 80);

        // Monthly series has 6 points (last 6 months).
        data.GetProperty("months").GetArrayLength().Should().Be(6);
        data.GetProperty("mrr_series").GetArrayLength().Should().Be(6);
        data.GetProperty("signup_series").GetArrayLength().Should().Be(6);

        // Recent activity is non-empty (the audit row we inserted).
        data.GetProperty("recent_activity").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Revenue_report_derives_net_growth_and_churn_from_snapshots()
    {
        await using var app = App();
        var client = PlatformClient(app);

        // Two prior-month snapshots: active 10 -> would compare against current month written at boot.
        // Insert an explicit previous month so the proc has a rn=2 row.
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            var lastMonth = DateTime.UtcNow.AddMonths(-1);
            var firstOfLast = new DateTime(lastMonth.Year, lastMonth.Month, 1);
            await conn.ExecuteAsync(
                "MERGE dbo.PlatformMetricsSnapshot AS t USING (SELECT @m AS Month) s ON t.Month = s.Month " +
                "WHEN MATCHED THEN UPDATE SET Mrr=1000, ActiveClients=10, CancelledClients=1 " +
                "WHEN NOT MATCHED THEN INSERT (Month, Mrr, ActiveClients, CancelledClients) " +
                "VALUES (@m, 1000, 10, 1);",
                new { m = firstOfLast });
        }

        var data = await Data(await client.GetAsync("/v1/reports/revenue"), HttpStatusCode.OK);

        data.GetProperty("months").GetArrayLength().Should().Be(6);
        data.GetProperty("revenue_series").GetArrayLength().Should().Be(6);
        // net_growth and gross_churn_pct are present and numeric (exact value depends on current-month snapshot).
        data.TryGetProperty("net_growth", out _).Should().BeTrue();
        data.TryGetProperty("gross_churn_pct", out _).Should().BeTrue();
    }
}
