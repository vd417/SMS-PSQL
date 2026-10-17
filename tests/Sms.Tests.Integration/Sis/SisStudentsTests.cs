using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Sis;

[Collection("sql")]
public class SisStudentsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["admin"], isPlatform: false);
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
    public async Task Student_create_get_list_and_update_within_tenant()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var created = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-1001", name = "Aarav Sharma", gender = "M",
            grade = "X", section = "A", roll = 1, guardian_name = "Priya", guardian_phone = "+91 90000 00000"
        }), HttpStatusCode.Created);

        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("class_label").GetString().Should().Be("X-A");
        created.GetProperty("status").GetString().Should().Be("active");
        created.GetProperty("fee_status").GetString().Should().Be("due");

        (await Data(await client.GetAsync($"/v1/students/{id}"), HttpStatusCode.OK))
            .GetProperty("name").GetString().Should().Be("Aarav Sharma");

        var list = await Data(await client.GetAsync("/v1/students?grade=X"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        var updated = await Data(await client.PatchAsJsonAsync($"/v1/students/{id}",
            new { fee_status = "paid", fee_due = 0 }), HttpStatusCode.OK);
        updated.GetProperty("fee_status").GetString().Should().Be("paid");
    }

    [Fact]
    public async Task Create_is_blocked_when_billed_seat_limit_is_reached()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        // Seed a tenant whose plan allows only 2 seats (Tenants/Plans are not RLS-guarded).
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\", \"LimitsStudents\") " +
                "VALUES (@tenantId, 'Cap School', 'cap-school', 'active', 2)",
                new { tenantId });
        }

        var client = TenantClient(app, tenantId);

        // First two enrolments fit within the 2-seat limit.
        for (int i = 1; i <= 2; i++)
            await Data(await client.PostAsJsonAsync("/v1/students", new
            {
                admission_no = $"CAP-{i}", name = $"Student {i}", gender = "M",
                grade = "X", section = "A", roll = i
            }), HttpStatusCode.Created);

        // Usage endpoint now reports the school is full.
        var usage = await Data(await client.GetAsync("/v1/students/seat-usage"), HttpStatusCode.OK);
        usage.GetProperty("limit").GetInt32().Should().Be(2);
        usage.GetProperty("used").GetInt32().Should().Be(2);
        usage.GetProperty("at_limit").GetBoolean().Should().BeTrue();
        usage.GetProperty("warn").GetBoolean().Should().BeTrue();

        // The third enrolment is hard-blocked.
        var blocked = await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "CAP-3", name = "Student 3", gender = "F",
            grade = "X", section = "A", roll = 3
        });
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("seat_limit_reached");
    }

    [Fact]
    public async Task Create_auto_grows_seats_for_per_student_school_instead_of_blocking()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Plans\" (\"Id\", \"Name\", \"Tier\", \"Pricing\", \"Price\", \"PerStudent\", \"LimitsStudents\") " +
                "VALUES (@planId, 'PerStudent', 'gold', 'per_student', 0, 10, 1)",
                new { planId });
            // 1-seat cap, but the CATRE team set this school to auto-grow.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\", \"PlanId\", \"LimitsStudents\", \"SeatIncreaseMode\") " +
                "VALUES (@tenantId, 'Auto School', 'auto-school', 'active', @planId, 1, 'auto')",
                new { tenantId, planId });
        }

        var client = TenantClient(app, tenantId);

        // Both enrolments succeed even past the 1-seat cap — the cap auto-grows.
        for (int i = 1; i <= 2; i++)
            await Data(await client.PostAsJsonAsync("/v1/students", new
            {
                admission_no = $"AUTO-{i}", name = $"Student {i}", gender = "M",
                grade = "X", section = "A", roll = i
            }), HttpStatusCode.Created);

        // Both enrolments landed (no 403) and the cap kept pace with enrolment.
        var usage = await Data(await client.GetAsync("/v1/students/seat-usage"), HttpStatusCode.OK);
        usage.GetProperty("used").GetInt32().Should().Be(2);
        usage.GetProperty("limit").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        // A further enrolment still succeeds — auto-grow continues rather than blocking.
        await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "AUTO-3", name = "Student 3", gender = "F", grade = "X", section = "A", roll = 3
        }), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_assigns_roll_az_by_name_and_ignores_client_roll()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var zoya = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-Z", name = "Zoya Khan", gender = "F",
            grade = "IV", section = "B", roll = 0
        }), HttpStatusCode.Created);
        zoya.GetProperty("roll").GetInt32().Should().Be(1);

        var asha = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-A", name = "Asha Kumar", gender = "F",
            grade = "IV", section = "B", roll = 99
        }), HttpStatusCode.Created);
        asha.GetProperty("roll").GetInt32().Should().Be(1);

        var zoyaId = zoya.GetProperty("id").GetGuid();
        (await Data(await client.GetAsync($"/v1/students/{zoyaId}"), HttpStatusCode.OK))
            .GetProperty("roll").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Students_are_isolated_across_tenants_by_rls()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientA = TenantClient(app, tenantA);
        var clientB = TenantClient(app, tenantB);

        var created = await Data(await clientA.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-A", name = "Tenant A Student", grade = "IX", section = "B", roll = 5
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        // Tenant B must NOT see tenant A's student
        (await clientB.GetAsync($"/v1/students/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bList = await Data(await clientB.GetAsync("/v1/students"), HttpStatusCode.OK);
        bList.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().NotContain(id);

        // Tenant A still sees its own
        var aList = await Data(await clientA.GetAsync("/v1/students"), HttpStatusCode.OK);
        aList.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);
    }

    [Fact]
    public async Task Students_require_authentication()
    {
        await using var app = App();
        (await app.CreateClient().GetAsync("/v1/students")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
