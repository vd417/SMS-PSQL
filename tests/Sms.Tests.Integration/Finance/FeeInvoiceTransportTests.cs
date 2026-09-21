using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Finance;

/// Covers GenerateInvoicesAsync's per-student transport-fee-head gating: a transport-flagged
/// fee head only contributes to a student's generated invoice when that student's active
/// StudentBusAssignments row carries that exact FeeHeadId. Every other fee head is unaffected.
[Collection("sql")]
public class FeeInvoiceTransportTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string AcademicYear = "2025-26";
    private const string Term = "Term 1";
    private const string ClassLabel = "X-A";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, because: body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> SeedRouteAsync(string cs, Guid tenantId, string name)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync(
            """INSERT INTO "dbo"."TransportRoutes" ("Id", "TenantId", "Name") VALUES (@Id, @TenantId, @Name)""",
            new { Id = id, TenantId = tenantId, Name = name });
        return id;
    }

    private static async Task<Guid> CreateStudentAsync(HttpClient client, string admissionNo, int roll)
    {
        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Transport Fee Kid " + admissionNo,
            grade = "X",
            section = "A",
            roll,
        }), HttpStatusCode.Created);
        return student.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateTransportHeadAsync(HttpClient client, string name)
    {
        var head = await Data(await client.PostAsJsonAsync("/v1/fees/heads", new
        {
            name,
            is_transport_fee_head = true,
        }), HttpStatusCode.Created);
        return head.GetProperty("id").GetGuid();
    }

    private static async Task OptIntoTransportAsync(HttpClient client, Guid studentId, Guid routeId, Guid feeHeadId)
    {
        await Data(await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new
        {
            opted_in = true,
            route_id = routeId,
            fee_head_id = feeHeadId,
        }), HttpStatusCode.OK);
    }

    private static async Task UpsertStructureAsync(HttpClient client, string amountsJson)
    {
        await Data(await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            name = "AY fees",
            academic_year = AcademicYear,
            currency = "INR",
            effective_from = "2025-04-01",
            status = "active",
            amounts_json = amountsJson,
        }), HttpStatusCode.OK);
    }

    private static async Task<JsonElement> GenerateInvoicesAsync(HttpClient client) =>
        await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = AcademicYear,
            term = Term,
            classes = new[] { ClassLabel },
        }), HttpStatusCode.OK);

    private static async Task<JsonElement> GetInvoiceAsync(HttpClient client, Guid studentId)
    {
        var list = await Data(await client.GetAsync($"/v1/fees/invoices?student_id={studentId}"), HttpStatusCode.OK);
        return list.EnumerateArray().Single(e => e.GetProperty("period").GetString() == $"{AcademicYear} {Term}");
    }

    [Fact]
    public async Task Transport_fee_head_included_for_student_with_matching_assignment()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-TRF-1", 1);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route TRF-1");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await OptIntoTransportAsync(client, studentId, routeId, transportHeadId);
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        invoice.GetProperty("amount").GetDecimal().Should().Be(1500);
    }

    [Fact]
    public async Task Transport_fee_head_excluded_for_student_with_no_assignment()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-TRF-2", 1);
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        invoice.GetProperty("amount").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task Transport_fee_head_excluded_when_assignment_is_different_fee_head()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-TRF-3", 1);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route TRF-3");
        var structureHeadId = await CreateTransportHeadAsync(client, "Transport A");
        var otherHeadId = await CreateTransportHeadAsync(client, "Transport B");
        await OptIntoTransportAsync(client, studentId, routeId, otherHeadId);
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{structureHeadId}\":500}}}}");

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        invoice.GetProperty("amount").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task Non_transport_fee_heads_included_for_both_opted_in_and_opted_out_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var optedInId = await CreateStudentAsync(client, "ADM-TRF-4A", 1);
        var optedOutId = await CreateStudentAsync(client, "ADM-TRF-4B", 2);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route TRF-4");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await OptIntoTransportAsync(client, optedInId, routeId, transportHeadId);
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        await GenerateInvoicesAsync(client);

        var optedInInvoice = await GetInvoiceAsync(client, optedInId);
        var optedOutInvoice = await GetInvoiceAsync(client, optedOutId);
        optedInInvoice.GetProperty("amount").GetDecimal().Should().Be(1500);
        optedOutInvoice.GetProperty("amount").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task Second_generate_call_does_not_duplicate_or_change_existing_invoice()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-TRF-5", 1);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route TRF-5");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await OptIntoTransportAsync(client, studentId, routeId, transportHeadId);
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        var first = await GenerateInvoicesAsync(client);
        first.GetProperty("created").GetInt32().Should().Be(1);
        var firstInvoice = await GetInvoiceAsync(client, studentId);

        var second = await GenerateInvoicesAsync(client);
        second.GetProperty("created").GetInt32().Should().Be(0);
        var secondInvoice = await GetInvoiceAsync(client, studentId);

        secondInvoice.GetProperty("id").GetGuid().Should().Be(firstInvoice.GetProperty("id").GetGuid());
        secondInvoice.GetProperty("amount").GetDecimal().Should().Be(1500);
    }

    [Fact]
    public async Task Two_students_same_class_get_different_amounts_based_on_transport_opt_in()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var transportKidId = await CreateStudentAsync(client, "ADM-TRF-6A", 1);
        var noTransportKidId = await CreateStudentAsync(client, "ADM-TRF-6B", 2);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route TRF-6");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await OptIntoTransportAsync(client, transportKidId, routeId, transportHeadId);
        await UpsertStructureAsync(client, $"{{\"X-A\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        await GenerateInvoicesAsync(client);

        var transportKidInvoice = await GetInvoiceAsync(client, transportKidId);
        var noTransportKidInvoice = await GetInvoiceAsync(client, noTransportKidId);
        transportKidInvoice.GetProperty("amount").GetDecimal().Should().NotBe(noTransportKidInvoice.GetProperty("amount").GetDecimal());
        transportKidInvoice.GetProperty("amount").GetDecimal().Should().Be(1500);
        noTransportKidInvoice.GetProperty("amount").GetDecimal().Should().Be(1000);
    }
}
