using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Phase5;

[Collection("sql")]
public class StudentParentTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(
        WebApplicationFactory<Program> app, Guid tenantId, string role, Guid? userId = null)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId ?? Guid.NewGuid(), tenantId, [role], isPlatform: false);
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

    [Fact]
    public async Task Student_homework_status_and_submit()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = TenantClient(app, tenantId, "admin");
        var admission = $"ADM-HW-{Guid.NewGuid():N}"[..20];

        var roster = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admission,
            name = "Homework Kid",
            grade = "X",
            section = "A",
            roll = 1,
        }), HttpStatusCode.Created);
        var studentId = roster.GetProperty("id").GetGuid();

        var studentUserId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                """
                INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status, Name)
                VALUES (@studentUserId, @tenantId, @admission, 0, N'active', N'Homework Kid');
                INSERT dbo.UserRoles (UserId, Role) VALUES (@studentUserId, N'student');
                """,
                new { studentUserId, tenantId, admission });
        }

        var created = await Data(await admin.PostAsJsonAsync("/v1/homework", new
        {
            student_id = studentId, title = "Problem Set 14 — Quadratics", due_date = "2026-07-10",
            due_time = "11:59 PM", priority = "high"
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("todo");

        var student = TenantClient(app, tenantId, "student", studentUserId);
        var progress = await Data(await student.PatchAsJsonAsync($"/v1/homework/{id}",
            new { status = "progress" }), HttpStatusCode.OK);
        progress.GetProperty("status").GetString().Should().Be("progress");

        var submitted = await Data(await student.PostAsync($"/v1/homework/{id}/submit", null), HttpStatusCode.OK);
        submitted.GetProperty("status").GetString().Should().Be("submitted");

        var list = await Data(await student.GetAsync($"/v1/homework?student_id={studentId}"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);
    }

    [Fact]
    public async Task Parent_pays_fee_invoice_via_gateway()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = TenantClient(app, tenantId, "admin");
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var roster = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-FEE-{Guid.NewGuid():N}"[..20],
            name = "Fee Ward",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);
        var childId = roster.GetProperty("id").GetGuid();

        Guid parentUserId;
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            parentUserId = await conn.QuerySingleAsync<Guid>(
                """
                SELECT Id FROM dbo.Users
                WHERE TenantId = @tenantId
                  AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@parentEmail)))
                """,
                new { tenantId, parentEmail });
        }

        var inv = await Data(await admin.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = childId, period = "Term 4 · 2026", due_date = "2026-05-05", amount = 1240
        }), HttpStatusCode.Created);
        var id = inv.GetProperty("id").GetGuid();
        inv.GetProperty("status").GetString().Should().Be("due");

        // Security fix: omitting Amount routes to the legacy placeholder gateway, which never
        // verifies a real transaction — a parent must not be able to mark their own invoice paid
        // for free this way. Only staff may use the amount-omitted path (e.g. to record a
        // payment collected outside the app); a parent's real payment goes through the
        // signature-verified Razorpay flow (see RazorpayFeePaymentAcceptanceTests), not this one.
        var parent = TenantClient(app, tenantId, Policies.StudentOrParent, parentUserId);
        (await parent.PostAsync($"/v1/fees/invoices/{id}/pay", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var paid = await Data(await admin.PostAsync($"/v1/fees/invoices/{id}/pay", null), HttpStatusCode.OK);
        paid.GetProperty("amount").GetDecimal().Should().Be(1240);
        paid.GetProperty("method").GetString().Should().NotBeNullOrEmpty();
        paid.GetProperty("invoice_id").GetGuid().Should().Be(id);

        (await admin.PostAsync($"/v1/fees/invoices/{id}/pay", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var invoices = await Data(await parent.GetAsync($"/v1/fees/invoices?student_id={childId}"), HttpStatusCode.OK);
        invoices.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == id)
            .GetProperty("status").GetString().Should().Be("paid");
    }
}
