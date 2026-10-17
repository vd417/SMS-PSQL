using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// <summary>
/// Seat-increase request flow: a school raises a request; the CATRE team approves it, which applies
/// the increase (cap + MRR + proration) and marks it approved.
/// </summary>
[Collection("sql")]
public class SeatRequestTests(PostgresFixture fx)
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
    public async Task Catre_approval_of_a_seat_request_applies_the_increase()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var planId = (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "PS-Req", tier = "gold", pricing = "per_student",
            price = 0, per_student = 10, min_students = 0, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 10, staff = 50, storage_gb = 20 },
            visibility = "published", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Req School", slug = "req-school", country = "IN",
            plan_id = planId, trial_days = 0, students_count = 10
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(10); // cap = purchased

        await Data(await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }),
            HttpStatusCode.OK);

        // A school raises a request (seeded directly; the owner-facing endpoint is tenant-scoped).
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"SeatIncreaseRequests\" (\"TenantId\",\"TenantName\",\"CurrentSeats\",\"RequestedSeats\") " +
                "VALUES (@id, 'Req School', 10, 20)",
                new { id });
        }

        // CATRE sees it pending.
        var pending = await Data(await client.GetAsync("/v1/seat-requests?status=pending"), HttpStatusCode.OK);
        var reqId = pending.EnumerateArray().First(r => r.GetProperty("tenant_id").GetGuid() == id)
            .GetProperty("id").GetGuid();

        // CATRE approves -> increase applied.
        var approved = await Data(
            await client.PostAsJsonAsync($"/v1/seat-requests/{reqId}/approve", new { note = "ok" }),
            HttpStatusCode.OK);
        approved.GetProperty("status").GetString().Should().Be("approved");

        var after = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        after.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(20);
        after.GetProperty("mrr").GetDecimal().Should().Be(200m); // 10/student x 20

        // Approving again is rejected (no longer pending).
        (await client.PostAsJsonAsync($"/v1/seat-requests/{reqId}/approve", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
