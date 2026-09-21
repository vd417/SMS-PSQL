using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class FeesTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), Guid.NewGuid(), ["admin"], isPlatform: false);
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

    private static async Task<JsonElement> Error(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, because: body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").Clone();
    }

    [Fact]
    public async Task Fee_invoice_generate_from_structure()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-FEE-1",
            name = "Fee Kid",
            grade = "X",
            section = "A",
            roll = 2,
        }), HttpStatusCode.Created);
        student.GetProperty("id").GetGuid().Should().NotBeEmpty();

        await Data(await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            name = "AY fees",
            academic_year = "2025-26",
            currency = "INR",
            effective_from = "2025-04-01",
            status = "active",
            amounts_json = """{"X-A":{"tuition":1000},"X":{"tuition":1000}}""",
        }), HttpStatusCode.OK);

        var gen = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = "2025-26",
            term = "Term 1",
            classes = new[] { "X-A" },
        }), HttpStatusCode.OK);
        gen.GetProperty("created").GetInt32().Should().BeGreaterThan(0);

        var list = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        list.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Fee_report_summary_returns_kpis_from_invoices_and_payments()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-SUM-1",
            name = "Summary Kid",
            grade = "X",
            section = "B",
            roll = 3,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId,
            period = "2025-26 Term 1",
            due_date = "2026-07-01",
            amount = 10000,
        }), HttpStatusCode.Created);

        await Data(await client.PostAsJsonAsync("/v1/fees/payments", new
        {
            student_id = studentId,
            student_name = "Summary Kid",
            class_label = "X-B",
            fee_type = "academic",
            amount = 4000,
            method = "UPI",
            @ref = "SUM-UPI-1",
        }), HttpStatusCode.Created);

        var summary = await Data(await client.GetAsync("/v1/fees/reports/summary"), HttpStatusCode.OK);
        summary.GetProperty("billed_term").GetDecimal().Should().Be(10000);
        summary.GetProperty("outstanding").GetDecimal().Should().Be(10000);
        summary.GetProperty("collected_term").GetDecimal().Should().Be(4000);
        summary.GetProperty("defaulters").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        summary.GetProperty("by_mode").GetArrayLength().Should().BeGreaterThan(0);
        summary.TryGetProperty("latest_payment", out var latest).Should().BeTrue();
        latest.ValueKind.Should().NotBe(JsonValueKind.Null);
        latest.GetProperty("amount").GetDecimal().Should().Be(4000);
    }

    [Fact]
    public async Task Fee_partial_payment_accumulates_paid_amount()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-PART-1",
            name = "Partial Kid",
            grade = "I",
            section = "A",
            roll = 1,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var inv = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId,
            period = "2025-26 Term 1",
            amount = 133055,
        }), HttpStatusCode.Created);
        var invoiceId = inv.GetProperty("id").GetGuid();

        await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            amount = 5000,
            mode = "Cash",
            student_name = "Partial Kid",
            cls = "I-A",
            fee_type = "All fee types",
        }), HttpStatusCode.OK);

        var list = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        var row = list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == invoiceId);
        row.GetProperty("status").GetString().Should().Be("partial");
        row.GetProperty("paid_amount").GetDecimal().Should().Be(5000);
        row.GetProperty("amount").GetDecimal().Should().Be(133055);
    }

    [Fact]
    public async Task Fee_payment_create_and_list_by_student()
    {
        await using var app = App();
        var client = TenantClient(app);
        var studentId = Guid.NewGuid();

        var created = await Data(await client.PostAsJsonAsync("/v1/fees/payments", new
        {
            student_id = studentId, student_name = "Aarav", class_label = "X-A",
            fee_type = "academic", amount = 14999, method = "UPI", @ref = "UPI-8842019"
        }), HttpStatusCode.Created);
        created.GetProperty("fee_type").GetString().Should().Be("academic");
        created.GetProperty("amount").GetDecimal().Should().Be(14999);
        created.GetProperty("ref").GetString().Should().Be("UPI-8842019");

        var list = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid())
            .Should().Contain(created.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Fee_invoice_generate_skips_duplicate_student_period()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-FEE-DUP-1",
            name = "Dup Kid",
            grade = "X",
            section = "A",
            roll = 2,
        }), HttpStatusCode.Created);

        await Data(await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            name = "AY fees",
            academic_year = "2025-26",
            currency = "INR",
            effective_from = "2025-04-01",
            status = "active",
            amounts_json = """{"X-A":{"tuition":1000},"X":{"tuition":1000}}""",
        }), HttpStatusCode.OK);

        var body = new
        {
            academic_year = "2025-26",
            term = "Term 1",
            classes = new[] { "X-A" },
        };
        var first = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", body), HttpStatusCode.OK);
        first.GetProperty("created").GetInt32().Should().BeGreaterThan(0);
        var second = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", body), HttpStatusCode.OK);
        second.GetProperty("created").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Fee_invoice_pay_persists_invoice_id_on_payment()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-PAY-LNK-1",
            name = "Link Kid",
            grade = "I",
            section = "A",
            roll = 1,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var inv = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId,
            period = "2025-26 Term Link",
            amount = 5000,
        }), HttpStatusCode.Created);
        var invoiceId = inv.GetProperty("id").GetGuid();

        var paid = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            amount = 5000,
            mode = "UPI",
            cls = "I-A",
            head_id = "tuition",
            head_name = "Tuition",
        }), HttpStatusCode.OK);
        var paymentId = paid.GetProperty("id").GetGuid();
        paid.GetProperty("invoice_id").GetGuid().Should().Be(invoiceId);
        paid.GetProperty("head_id").GetString().Should().Be("tuition");
        paid.GetProperty("method").GetString().Should().Be("UPI");
        paid.GetProperty("class_label").GetString().Should().Be("I-A");
        paid.GetProperty("fee_type").GetString().Should().Be("Tuition");

        var listed = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        listed.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == paymentId)
            .GetProperty("invoice_id").GetGuid().Should().Be(invoiceId);

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenant::text, false)", new { tenant });
        var row = await conn.QuerySingleAsync<(Guid InvoiceId, string HeadId, string Method)>(
            """SELECT "InvoiceId", "HeadId", "Method" FROM "dbo"."FeePayments" WHERE "Id" = @paymentId""",
            new { paymentId });
        row.InvoiceId.Should().Be(invoiceId);
        row.HeadId.Should().Be("tuition");
        row.Method.Should().Be("UPI");
    }

    [Fact]
    public async Task Pay_invoice_with_same_idempotency_key_twice_records_payment_once()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-IDEMP-1", name = "Idem Kid", grade = "V", section = "A", roll = 9,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var invoice = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId, period = "Term 1", due_date = "2026-06-01", amount = 5000,
        }), HttpStatusCode.Created);
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var body = new
        {
            amount = 2000, mode = "Cash", student_name = "Idem Kid", cls = "V-A",
            fee_type = "academic", idempotency_key = idempotencyKey,
        };

        var first = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body), HttpStatusCode.OK);
        var second = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body), HttpStatusCode.OK);

        first.GetProperty("id").GetGuid().Should().Be(second.GetProperty("id").GetGuid());

        var payments = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        payments.EnumerateArray().Count(p => p.GetProperty("id").GetGuid() == first.GetProperty("id").GetGuid())
            .Should().Be(1);

        var invoiceAfter = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        invoiceAfter.EnumerateArray().First(i => i.GetProperty("id").GetGuid() == invoiceId)
            .GetProperty("paid_amount").GetDecimal().Should().Be(2000);
    }

    [Fact]
    public async Task Pay_invoice_writes_exactly_one_audit_row()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-AUDIT-1", name = "Audit Kid", grade = "V", section = "B", roll = 4,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();
        var invoice = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId, period = "Term 1", due_date = "2026-06-01", amount = 3000,
        }), HttpStatusCode.Created);
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var paid = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            amount = 3000, mode = "Cash", student_name = "Audit Kid", cls = "V-B", fee_type = "academic",
        }), HttpStatusCode.OK);
        var paymentId = paid.GetProperty("id").GetString();

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenant::text, false)", new { tenant });
        var count = await conn.QuerySingleAsync<int>(
            """SELECT COUNT(*) FROM "dbo"."AuditLogs" WHERE "EntityType" = 'FeePayment' AND "EntityId" = @paymentId""",
            new { paymentId });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Pay_invoice_retry_after_completing_payment_returns_same_payment_not_conflict()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-IDEMP-2", name = "Idem Full Kid", grade = "V", section = "A", roll = 10,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var invoice = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId, period = "Term 1", due_date = "2026-06-01", amount = 2000,
        }), HttpStatusCode.Created);
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var body = new
        {
            amount = 2000, mode = "Cash", student_name = "Idem Full Kid", cls = "V-A",
            fee_type = "academic", idempotency_key = idempotencyKey,
        };

        /* First call fully pays the invoice off (Status becomes "paid"). */
        var first = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body), HttpStatusCode.OK);

        var invoiceAfter = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        invoiceAfter.EnumerateArray().First(i => i.GetProperty("id").GetGuid() == invoiceId)
            .GetProperty("status").GetString().Should().Be("paid");

        /* Retry with the same idempotency key against a now-fully-paid invoice must still return
           the original payment (200), never the generic "invoice already paid" 409. */
        var retryResponse = await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body);
        var second = await Data(retryResponse, HttpStatusCode.OK);
        second.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid());

        var payments = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        payments.EnumerateArray().Count(p => p.GetProperty("id").GetGuid() == first.GetProperty("id").GetGuid())
            .Should().Be(1);
    }

    [Fact]
    public async Task Pay_invoice_concurrent_requests_with_same_idempotency_key_record_payment_once()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-IDEMP-RACE-1", name = "Race Kid", grade = "V", section = "C", roll = 11,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var invoice = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId, period = "Term 1", due_date = "2026-06-01", amount = 5000,
        }), HttpStatusCode.Created);
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var body = new
        {
            amount = 1500, mode = "Cash", student_name = "Race Kid", cls = "V-C",
            fee_type = "academic", idempotency_key = idempotencyKey,
        };

        /* Fire two requests carrying the same key concurrently. Regardless of whether both reach
           RecordInvoicePaymentAsync's INSERT before either commits (exercising the unique-index
           catch path) or the second lands after the first commits (exercising the fast-path/
           top-of-transaction lookup), the observable contract must hold: both calls succeed with
           the same payment id, and exactly one payment row is ever recorded. */
        var task1 = client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body);
        var task2 = client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", body);
        var responses = await Task.WhenAll(task1, task2);

        var first = await Data(responses[0], HttpStatusCode.OK);
        var second = await Data(responses[1], HttpStatusCode.OK);
        first.GetProperty("id").GetGuid().Should().Be(second.GetProperty("id").GetGuid());

        var payments = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        payments.EnumerateArray().Count(p => p.GetProperty("id").GetGuid() == first.GetProperty("id").GetGuid())
            .Should().Be(1);
    }

    [Fact]
    public async Task Create_manual_payment_with_same_idempotency_key_twice_returns_same_row()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-MANUAL-1", name = "Manual Kid", grade = "VI", section = "A", roll = 3,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var body = new
        {
            student_id = studentId, student_name = "Manual Kid", class_label = "VI-A",
            fee_type = "academic", amount = 1500, method = "Cash", idempotency_key = idempotencyKey,
        };

        var first = await Data(await client.PostAsJsonAsync("/v1/fees/payments", body), HttpStatusCode.Created);
        var second = await Data(await client.PostAsJsonAsync("/v1/fees/payments", body), HttpStatusCode.Created);
        first.GetProperty("id").GetGuid().Should().Be(second.GetProperty("id").GetGuid());

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenant::text, false)", new { tenant });
        var paymentCount = await conn.QuerySingleAsync<int>(
            """SELECT COUNT(*) FROM "dbo"."FeePayments" WHERE "Id" = @id""",
            new { id = first.GetProperty("id").GetGuid() });
        paymentCount.Should().Be(1);
        var auditCount = await conn.QuerySingleAsync<int>(
            """SELECT COUNT(*) FROM "dbo"."AuditLogs" WHERE "EntityType" = 'FeePayment' AND "EntityId" = @id""",
            new { id = first.GetProperty("id").GetString() });
        auditCount.Should().Be(1);
    }

    [Fact]
    public async Task Pay_invoice_reusing_idempotency_key_with_different_amount_returns_409_not_stale_payment()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-IDEMP-CONFLICT-1", name = "Conflict Kid", grade = "V", section = "A", roll = 12,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var invoice = await Data(await client.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = studentId, period = "Term 1", due_date = "2026-06-01", amount = 5000,
        }), HttpStatusCode.Created);
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var first = await Data(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            amount = 2000, mode = "Cash", student_name = "Conflict Kid", cls = "V-A",
            fee_type = "academic", idempotency_key = idempotencyKey,
        }), HttpStatusCode.OK);

        /* Same key, but the amount was edited before resubmitting (e.g. after the first response
           was lost to a network blip). Must be rejected — never silently return the 2000 payment
           as though it satisfied the new 1500 request. */
        var err = await Error(await client.PostAsJsonAsync($"/v1/fees/invoices/{invoiceId}/pay", new
        {
            amount = 1500, mode = "Cash", student_name = "Conflict Kid", cls = "V-A",
            fee_type = "academic", idempotency_key = idempotencyKey,
        }), HttpStatusCode.Conflict);
        err.GetProperty("code").GetString().Should().Be("idempotency_key_reused");

        var payments = await Data(await client.GetAsync($"/v1/fees/payments?student_id={studentId}"), HttpStatusCode.OK);
        payments.EnumerateArray().Should().ContainSingle();
        payments.EnumerateArray().First().GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid());
        payments.EnumerateArray().First().GetProperty("amount").GetDecimal().Should().Be(2000);

        var invoiceAfter = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        invoiceAfter.EnumerateArray().First(i => i.GetProperty("id").GetGuid() == invoiceId)
            .GetProperty("paid_amount").GetDecimal().Should().Be(2000);
    }

    [Fact]
    public async Task Create_manual_payment_reusing_idempotency_key_with_different_amount_returns_409_not_stale_payment()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenant, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-MANUAL-CONFLICT-1", name = "Manual Conflict Kid", grade = "VI", section = "A", roll = 5,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var idempotencyKey = Guid.NewGuid();
        var first = await Data(await client.PostAsJsonAsync("/v1/fees/payments", new
        {
            student_id = studentId, student_name = "Manual Conflict Kid", class_label = "VI-A",
            fee_type = "academic", amount = 1500, method = "Cash", idempotency_key = idempotencyKey,
        }), HttpStatusCode.Created);

        var err = await Error(await client.PostAsJsonAsync("/v1/fees/payments", new
        {
            student_id = studentId, student_name = "Manual Conflict Kid", class_label = "VI-A",
            fee_type = "academic", amount = 900, method = "Cash", idempotency_key = idempotencyKey,
        }), HttpStatusCode.Conflict);
        err.GetProperty("code").GetString().Should().Be("idempotency_key_reused");

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenant::text, false)", new { tenant });
        var paymentCount = await conn.QuerySingleAsync<int>(
            """SELECT COUNT(*) FROM "dbo"."FeePayments" WHERE "TenantId" = @tenant AND "IdempotencyKey" = @idempotencyKey""",
            new { tenant, idempotencyKey });
        paymentCount.Should().Be(1);
        var amount = await conn.QuerySingleAsync<decimal>(
            """SELECT "Amount" FROM "dbo"."FeePayments" WHERE "Id" = @id""", new { id = first.GetProperty("id").GetGuid() });
        amount.Should().Be(1500);
    }
}
