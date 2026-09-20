using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Finance;

/// Covers the per-fee-head invoice line breakdown persisted by GenerateInvoicesAsync
/// (M0192_FeeInvoiceLines_Table): every fee head that contributed to a generated invoice's
/// total is recorded as its own line, snapshotted at generation time so later Fee Head /
/// Fee Structure / transport-mapping edits never rewrite an already-generated invoice.
[Collection("sql")]
public class FeeInvoiceLinesTests(PostgresFixture fx)
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
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name });
        return id;
    }

    private static async Task<Guid> CreateStudentAsync(HttpClient client, string admissionNo, int roll)
    {
        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Line Test Kid " + admissionNo,
            grade = "X",
            section = "A",
            roll,
        }), HttpStatusCode.Created);
        return student.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateHeadAsync(HttpClient client, string name, bool isTransport = false, string? description = null)
    {
        var head = await Data(await client.PostAsJsonAsync("/v1/fees/heads", new
        {
            name,
            is_transport_fee_head = isTransport,
            description,
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

    private static string AmountsJson(params (Guid HeadId, decimal Amount)[] heads) =>
        JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, decimal>>
        {
            [ClassLabel] = heads.ToDictionary(h => h.HeadId.ToString(), h => h.Amount),
        });

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

    private static decimal LineAmount(JsonElement invoice, string headName) =>
        invoice.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("head_name").GetString() == headName)
            .GetProperty("amount").GetDecimal();

    [Fact]
    public async Task Generated_invoice_persists_one_line_per_contributing_fee_head()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-1", 1);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Lines-1");
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        var examId = await CreateHeadAsync(client, "Exam Fee");
        var transportId = await CreateHeadAsync(client, "Transport Fee", isTransport: true);
        await OptIntoTransportAsync(client, studentId, routeId, transportId);
        await UpsertStructureAsync(client,
            AmountsJson((tuitionId, 1000), (transportId, 500), (examId, 200)));

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        invoice.GetProperty("amount").GetDecimal().Should().Be(1700);
        var lines = invoice.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(3);
        LineAmount(invoice, "Tuition Fee").Should().Be(1000);
        LineAmount(invoice, "Transport Fee").Should().Be(500);
        LineAmount(invoice, "Exam Fee").Should().Be(200);
    }

    [Fact]
    public async Task A_line_for_a_head_deleted_before_generation_gets_a_readable_label_not_its_raw_guid()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-DEL", 1);
        var tripId = await CreateHeadAsync(client, "Trip Fee");
        await UpsertStructureAsync(client, AmountsJson((tripId, 1000)));

        (await client.DeleteAsync($"/v1/fees/heads/{tripId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        var line = invoice.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("head_name").GetString().Should().NotBe(tripId.ToString(),
            "a deleted head must not surface its raw GUID as the invoice line's label");
        line.GetProperty("head_name").GetString().Should().Contain("Deleted fee head");
        line.GetProperty("amount").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task A_head_s_description_snapshots_onto_the_invoice_line_and_survives_the_head_being_redescribed()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-DESC", 1);
        var tripId = await CreateHeadAsync(client, "Trip Fee", description: "Annual educational trip to Mumbai");
        await UpsertStructureAsync(client, AmountsJson((tripId, 1000)));

        await GenerateInvoicesAsync(client);

        // Redescribing the head afterward must not change the already-generated invoice's line.
        (await client.PatchAsJsonAsync($"/v1/fees/heads/{tripId}", new { description = "Trip cancelled" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var invoice = await GetInvoiceAsync(client, studentId);
        var line = invoice.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("description").GetString().Should().Be("Annual educational trip to Mumbai",
            "the line snapshots the head's description at generation time, unaffected by later edits");
    }

    [Fact]
    public async Task Invoice_total_always_equals_the_sum_of_its_lines()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-2", 1);
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        var examId = await CreateHeadAsync(client, "Exam Fee");
        await UpsertStructureAsync(client, AmountsJson((tuitionId, 1000), (examId, 200)));

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        var sumOfLines = invoice.GetProperty("lines").EnumerateArray().Sum(l => l.GetProperty("amount").GetDecimal());
        sumOfLines.Should().Be(invoice.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Student_without_transport_assignment_gets_only_non_transport_lines()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-3", 1);
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        var examId = await CreateHeadAsync(client, "Exam Fee");
        var transportId = await CreateHeadAsync(client, "Transport Fee", isTransport: true);
        await UpsertStructureAsync(client,
            AmountsJson((tuitionId, 1000), (transportId, 500), (examId, 200)));

        await GenerateInvoicesAsync(client);

        var invoice = await GetInvoiceAsync(client, studentId);
        invoice.GetProperty("amount").GetDecimal().Should().Be(1200);
        var headNames = invoice.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("head_name").GetString()).ToList();
        headNames.Should().BeEquivalentTo(["Tuition Fee", "Exam Fee"]);
    }

    [Fact]
    public async Task Historical_invoice_lines_are_unaffected_by_a_later_fee_head_rename()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-4", 1);
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        await UpsertStructureAsync(client, AmountsJson((tuitionId, 1000)));
        await GenerateInvoicesAsync(client);

        var before = await GetInvoiceAsync(client, studentId);
        LineAmount(before, "Tuition Fee").Should().Be(1000);

        await Data(await client.PatchAsJsonAsync($"/v1/fees/heads/{tuitionId}", new { name = "Renamed Tuition" }),
            HttpStatusCode.OK);

        var after = await GetInvoiceAsync(client, studentId);
        LineAmount(after, "Tuition Fee").Should().Be(1000, "the invoice line snapshot must not follow a later Fee Head rename");
        after.GetProperty("lines").EnumerateArray()
            .Any(l => l.GetProperty("head_name").GetString() == "Renamed Tuition").Should().BeFalse();
    }

    [Fact]
    public async Task Historical_invoice_is_unaffected_by_a_later_transport_mapping_change()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-5", 1);
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Lines-5");
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        var transportId = await CreateHeadAsync(client, "Transport Fee", isTransport: true);
        await OptIntoTransportAsync(client, studentId, routeId, transportId);
        await UpsertStructureAsync(client, AmountsJson((tuitionId, 1000), (transportId, 500)));
        await GenerateInvoicesAsync(client);

        var before = await GetInvoiceAsync(client, studentId);
        before.GetProperty("amount").GetDecimal().Should().Be(1500);

        // Student later opts out of transport entirely.
        await Data(await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new { opted_in = false }),
            HttpStatusCode.OK);

        var after = await GetInvoiceAsync(client, studentId);
        after.GetProperty("amount").GetDecimal().Should().Be(1500, "opting out later must affect future invoices only, not this already-generated one");
        after.GetProperty("lines").EnumerateArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Recording_a_payment_marks_the_existing_invoice_paid_without_creating_a_second_one_or_changing_lines()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-6", 1);
        var tuitionId = await CreateHeadAsync(client, "Tuition Fee");
        var examId = await CreateHeadAsync(client, "Exam Fee");
        await UpsertStructureAsync(client, AmountsJson((tuitionId, 1000), (examId, 200)));
        await GenerateInvoicesAsync(client);

        var before = await GetInvoiceAsync(client, studentId);
        var invoiceId = before.GetProperty("id").GetGuid();

        await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            method = "Cash",
            student_name = "Line Test Kid ADM-LINES-6",
            cls = "X-A",
            fee_type = "All fee types",
        }), HttpStatusCode.OK);

        var list = await Data(await client.GetAsync($"/v1/fees/invoices?student_id={studentId}"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(1, "an offline payment must mark the existing invoice paid, not create a second one");

        var after = list.EnumerateArray().Single();
        after.GetProperty("id").GetGuid().Should().Be(invoiceId);
        after.GetProperty("status").GetString().Should().Be("paid");
        after.GetProperty("amount").GetDecimal().Should().Be(1200);
        after.GetProperty("lines").EnumerateArray().Should().HaveCount(2);
        LineAmount(after, "Tuition Fee").Should().Be(1000);
        LineAmount(after, "Exam Fee").Should().Be(200);
    }

    [Fact]
    public async Task Manually_created_invoice_without_lines_still_lists_with_an_empty_lines_array()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var studentId = await CreateStudentAsync(client, "ADM-LINES-7", 1);
        await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId,
            period = "2025-26 Term Manual",
            amount = 5000,
        }), HttpStatusCode.Created);

        var list = await Data(await client.GetAsync($"/v1/fees/invoices?student_id={studentId}"), HttpStatusCode.OK);
        var invoice = list.EnumerateArray().Single();
        invoice.GetProperty("amount").GetDecimal().Should().Be(5000);
        invoice.GetProperty("lines").GetArrayLength().Should().Be(0);
    }
}
