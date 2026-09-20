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

namespace Sms.Tests.Integration.Sis;

/// Covers the real Aarav scenario: a bulk-imported student already has an invoice for a period
/// (created by reconciliation/backfill BEFORE transport was known), then gets manually mapped to
/// transport afterward. The existing invoice must be recalculated in place — not duplicated —
/// while a payment already recorded against an invoice must make it completely untouchable.
[Collection("sql")]
public class FeeRecalculationOnTransportChangeTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string AcademicYear = "2026-27";
    private const string ClassLabel = "I-A";

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
            effective_from = "2026-04-01",
            status = "active",
            amounts_json = amountsJson,
        }), HttpStatusCode.OK);
    }

    /// Bulk-import ONE student with no transport row at all in the payload (mirrors "the student
    /// does not need transport info in the import file" — transport is added later, manually).
    private static async Task<Guid> BulkImportStudentAsync(HttpClient client, string admissionNo)
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
                        grade = "I",
                        section = "A",
                        roll = 1,
                        guardian_name = "Parent " + admissionNo,
                        guardian_phone = "9000000001",
                        email = admissionNo.ToLowerInvariant() + "@example.com",
                        gender = "M",
                        dob = "2019-04-23",
                    },
                    extras_json = "{}",
                    transport = (object?)null,
                },
            },
        }), HttpStatusCode.OK);

        var row = response.GetProperty("rows").EnumerateArray().Single();
        row.GetProperty("status").GetString().Should().Be("created", because: row.ToString());
        return row.GetProperty("student_id").GetGuid();
    }

    private static Task<HttpResponseMessage> MapTransportAsync(
        HttpClient client, Guid studentId, Guid routeId, Guid feeHeadId) =>
        client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new
        {
            opted_in = true,
            route_id = routeId,
            fee_head_id = feeHeadId,
        });

    private sealed record InvoiceRow(Guid Id, Guid StudentId, string Period, decimal Amount, decimal PaidAmount);
    private sealed record LineRow(Guid InvoiceId, string HeadName, decimal Amount);

    private static async Task<InvoiceRow> GetInvoiceAsync(string cs, Guid tenantId, Guid studentId)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        return (await conn.QueryAsync<InvoiceRow>(
            "SELECT Id, StudentId, Period, Amount, PaidAmount FROM dbo.FeeInvoices WHERE StudentId = @studentId",
            new { studentId })).Single();
    }

    private static async Task<IReadOnlyList<LineRow>> GetLinesAsync(string cs, Guid tenantId, Guid invoiceId)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        var rows = await conn.QueryAsync<LineRow>(
            "SELECT InvoiceId, FeeHeadName AS HeadName, Amount FROM dbo.FeeInvoiceLines WHERE InvoiceId = @invoiceId",
            new { invoiceId });
        return rows.ToList();
    }

    [Fact]
    public async Task Mapping_transport_after_the_invoice_already_exists_recalculates_it_exactly_once_with_no_duplicate_line()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Aarav");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client,
            $"{{\"{ClassLabel}\":{{\"tuition\":17000,\"exam\":6300,\"{transportHeadId}\":500}}}}");

        // Aarav: bulk-imported with no transport info at all (the file doesn't need it).
        var aaravId = await BulkImportStudentAsync(client, "ADM-AARAV");

        // An admin runs Generate Invoices for I-A before transport is ever mapped — Aarav gets
        // an invoice with no transport line, exactly the real incident's sequence of events.
        await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = AcademicYear,
            term = "Term 1",
            classes = new[] { ClassLabel },
        }), HttpStatusCode.OK);

        var beforeTransport = await GetInvoiceAsync(fx.ConnectionString, tenantId, aaravId);
        beforeTransport.Amount.Should().Be(23300); // 17000 + 6300, no transport yet
        beforeTransport.PaidAmount.Should().Be(0);
        var invoiceId = beforeTransport.Id;

        // Now map Aarav to transport, manually, after the invoice already exists.
        var mapResult = await MapTransportAsync(client, aaravId, routeId, transportHeadId);
        mapResult.StatusCode.Should().Be(HttpStatusCode.OK);

        // The SAME invoice (same Id) is recalculated to include the transport line.
        var afterTransport = await GetInvoiceAsync(fx.ConnectionString, tenantId, aaravId);
        afterTransport.Id.Should().Be(invoiceId, because: "recalculation must update the existing invoice, never create a second one");
        afterTransport.Amount.Should().Be(23800); // 17000 + 6300 + 500
        var lines = await GetLinesAsync(fx.ConnectionString, tenantId, invoiceId);
        lines.Count(l => l.HeadName == "Transport").Should().Be(1, because: "the transport line must appear exactly once");

        // Re-mapping the SAME transport again must not duplicate the line or change the amount.
        var secondMap = await MapTransportAsync(client, aaravId, routeId, transportHeadId);
        secondMap.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterSecondMap = await GetInvoiceAsync(fx.ConnectionString, tenantId, aaravId);
        afterSecondMap.Id.Should().Be(invoiceId);
        afterSecondMap.Amount.Should().Be(23800);
        var linesAfterSecondMap = await GetLinesAsync(fx.ConnectionString, tenantId, invoiceId);
        linesAfterSecondMap.Count(l => l.HeadName == "Transport").Should().Be(1);
        linesAfterSecondMap.Should().HaveCount(3, because: "no duplicate lines of any kind after re-mapping the same transport twice");
    }

    [Fact]
    public async Task An_invoice_with_ANY_payment_recorded_is_never_recalculated_even_after_transport_changes()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Paid");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client,
            $"{{\"{ClassLabel}\":{{\"tuition\":17000,\"exam\":6300,\"{transportHeadId}\":500}}}}");

        var studentId = await BulkImportStudentAsync(client, "ADM-PAID");
        await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = AcademicYear,
            term = "Term 1",
            classes = new[] { ClassLabel },
        }), HttpStatusCode.OK);

        var invoiceBefore = await GetInvoiceAsync(fx.ConnectionString, tenantId, studentId);
        invoiceBefore.Amount.Should().Be(23300);

        // Record a PARTIAL payment against it before transport is mapped.
        await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceBefore.Id}/pay", new
        {
            amount = 5000,
        }), HttpStatusCode.OK);

        var invoiceAfterPayment = await GetInvoiceAsync(fx.ConnectionString, tenantId, studentId);
        invoiceAfterPayment.PaidAmount.Should().Be(5000);

        // Now map transport — the invoice must be left completely untouched: same amount.
        var mapResult = await MapTransportAsync(client, studentId, routeId, transportHeadId);
        mapResult.StatusCode.Should().Be(HttpStatusCode.OK);

        var invoiceAfterTransport = await GetInvoiceAsync(fx.ConnectionString, tenantId, studentId);
        invoiceAfterTransport.Id.Should().Be(invoiceBefore.Id);
        invoiceAfterTransport.Amount.Should().Be(23300, because: "an invoice with ANY payment recorded must never be silently mutated");
        invoiceAfterTransport.PaidAmount.Should().Be(5000);
        var lines = await GetLinesAsync(fx.ConnectionString, tenantId, invoiceBefore.Id);
        lines.Should().NotContain(l => l.HeadName == "Transport", because: "the transport line must not be added to a part-paid invoice");
    }
}
