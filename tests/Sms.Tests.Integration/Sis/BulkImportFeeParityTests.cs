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

namespace Sms.Tests.Integration.Sis;

/// Empirical proof that a student created through the bulk-import batch endpoint ends up
/// exactly as fee-eligible as one created through single Add — both flow through the same
/// CreateStudentAsync / StudentTransportService.SetAsync calls and the same generic, roster-wide
/// GenerateInvoicesAsync query, so nothing about the creation path should change the result.
[Collection("sql")]
public class BulkImportFeeParityTests(PostgresFixture fx)
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
            "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name });
        return id;
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

    /// Single Add's own path: POST /v1/students, then PUT .../transport — same two calls
    /// studentAdd.tsx makes.
    private static async Task<Guid> CreateManualStudentWithTransportAsync(
        HttpClient client, string admissionNo, int roll, Guid routeId, Guid feeHeadId)
    {
        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Manual Kid " + admissionNo,
            grade = "X",
            section = "A",
            roll,
            guardian_name = "Parent " + admissionNo,
            guardian_phone = "9000000001",
            email = admissionNo.ToLowerInvariant() + "@example.com",
            gender = "M",
            dob = "2015-04-23",
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        await Data(await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new
        {
            opted_in = true,
            route_id = routeId,
            fee_head_id = feeHeadId,
        }), HttpStatusCode.OK);

        return studentId;
    }

    /// Bulk import's own path: POST /v1/students/bulk-import/batch with one row — same shape
    /// buildBulkImportPayloads() in sis.tsx sends.
    private static async Task<Guid> CreateBulkImportedStudentWithTransportAsync(
        HttpClient client, string admissionNo, int roll, Guid routeId, Guid feeHeadId)
    {
        var response = await Data(await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = Guid.NewGuid(),
            batch_index = 0,
            rows = new object[]
            {
                new
                {
                    row_number = 2,
                    create_student_request = new
                    {
                        admission_no = admissionNo,
                        name = "Bulk Kid " + admissionNo,
                        grade = "X",
                        section = "A",
                        roll,
                        guardian_name = "Parent " + admissionNo,
                        guardian_phone = "9000000002",
                        email = admissionNo.ToLowerInvariant() + "@example.com",
                        gender = "M",
                        dob = "2015-04-23",
                    },
                    extras_json = "{}",
                    transport = new
                    {
                        opted_in = true,
                        route_id = routeId,
                        fee_head_id = feeHeadId,
                    },
                },
            },
        }), HttpStatusCode.OK);

        var row = response.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("status").GetString().Should().Be("created", because: row.ToString());
        return row.GetProperty("student_id").GetGuid();
    }

    [Fact]
    public async Task Manually_added_and_bulk_imported_students_get_identical_invoices_for_the_same_class_and_transport()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Parity");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client, $"{{\"{ClassLabel}\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        var manualStudentId = await CreateManualStudentWithTransportAsync(
            client, "ADM-PARITY-MANUAL", 1, routeId, transportHeadId);
        var bulkStudentId = await CreateBulkImportedStudentWithTransportAsync(
            client, "ADM-PARITY-BULK", 2, routeId, transportHeadId);

        var result = await GenerateInvoicesAsync(client);
        result.GetProperty("created").GetInt32().Should().Be(2);

        var manualInvoice = await GetInvoiceAsync(client, manualStudentId);
        var bulkInvoice = await GetInvoiceAsync(client, bulkStudentId);

        // Both opted into transport with the same route/fee head under the same class + fee
        // structure — creation path must not change the outcome.
        manualInvoice.GetProperty("amount").GetDecimal().Should().Be(1500);
        bulkInvoice.GetProperty("amount").GetDecimal().Should().Be(manualInvoice.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Rerunning_generate_invoices_after_a_bulk_import_does_not_duplicate_the_bulk_students_invoice()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Parity Retry");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client, $"{{\"{ClassLabel}\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        var bulkStudentId = await CreateBulkImportedStudentWithTransportAsync(
            client, "ADM-PARITY-RETRY", 1, routeId, transportHeadId);

        var first = await GenerateInvoicesAsync(client);
        first.GetProperty("created").GetInt32().Should().Be(1);
        var firstInvoice = await GetInvoiceAsync(client, bulkStudentId);

        var second = await GenerateInvoicesAsync(client);
        second.GetProperty("created").GetInt32().Should().Be(0);
        var secondInvoice = await GetInvoiceAsync(client, bulkStudentId);

        secondInvoice.GetProperty("id").GetGuid().Should().Be(firstInvoice.GetProperty("id").GetGuid());
    }
}
