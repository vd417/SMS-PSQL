using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Authz;

[Collection("sql")]
public class SecurityIdorTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(
        WebApplicationFactory<Program> app, Guid tenantId, string role, Guid? userId = null)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId ?? Guid.NewGuid(), tenantId, [role], isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Parent_cannot_create_fee_invoice_or_list_unscoped_payments()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var parent = Client(app, tenantId, "parent");

        (await parent.PostAsJsonAsync("/v1/fees/invoices", new
        {
            student_id = Guid.NewGuid(), period = "Term 1", amount = 100
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await parent.GetAsync("/v1/fees/payments")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await parent.GetAsync("/v1/fees/invoices")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parent_cannot_start_a_trip()
    {
        await using var app = App();
        var parent = Client(app, Guid.NewGuid(), "parent");
        (await parent.PostAsJsonAsync("/v1/staff/trips", new { direction = "pickup", bus_no = "KA-01" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parent_cannot_create_notifications_or_update_complaints()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var parent = Client(app, tenantId, "parent");
        var admin = Client(app, tenantId, "admin");

        (await parent.PostAsJsonAsync("/v1/notifications",
            new { icon = "bell", tone = "info", title = "x", body = "y" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var created = await admin.PostAsJsonAsync("/v1/complaints", new
        {
            subject = "Bus late", from = "Parent", category = "Transport", priority = "high", body = "delayed"
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        (await parent.PatchAsJsonAsync($"/v1/complaints/{id}", new { status = "resolved" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Non_staff_payslip_user_id_is_ignored()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffUser = Guid.NewGuid();
        var parentUser = Guid.NewGuid();
        var admin = Client(app, tenantId, "admin", staffUser);
        var parent = Client(app, tenantId, "parent", parentUser);

        await admin.PostAsJsonAsync("/v1/payslips", new
        {
            user_id = staffUser, month = "April", year = 2026, gross = 60000, deductions = 8000, net = 52000
        });

        var res = await parent.GetAsync($"/v1/payslips?user_id={staffUser}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Parent_cannot_create_or_patch_students_and_classes()
    {
        await using var app = App();
        var parent = Client(app, Guid.NewGuid(), "parent");
        (await parent.PostAsJsonAsync("/v1/students", new { name = "X", admission_no = "A1" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await parent.PostAsJsonAsync("/v1/classes", new { name = "X-A", grade = "X", section = "A" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
