using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Auth;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class ApprovalsStudentDetailsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> MakeApp(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task Approvals_list_includes_student_details_for_a_child_leave()
    {
        var app = MakeApp(fx);
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();   // the parent
        var studentId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Asha Parent')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"ClassLabel\", \"Roll\", \"Status\") " +
                "VALUES (@studentId, @tenantId, 'ADM-012', 'Rahul Sharma', 'Grade 5', 'A', 'Grade 5 - A', 12, 'active')",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"ChildId\", \"Type\", \"Status\") " +
                "VALUES (@tenantId, @requesterId, @studentId, 'sick', 'pending')",
                new { tenantId, requesterId, studentId });
        }

        var jwt = new JwtTokenService(new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 }, new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/approvals?status=pending");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data")[0];
        row.GetProperty("student_name").GetString().Should().Be("Rahul Sharma");
        row.GetProperty("admission_no").GetString().Should().Be("ADM-012");
        row.GetProperty("student_class").GetString().Should().Be("Grade 5 - A");
        row.GetProperty("student_section").GetString().Should().Be("A");
        row.GetProperty("student_roll").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Staff_self_leave_has_null_student_fields()
    {
        var app = MakeApp(fx);
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Rajesh Teacher')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"Type\", \"Status\") VALUES (@tenantId, @requesterId, 'casual', 'pending')",
                new { tenantId, requesterId });
        }

        var jwt = new JwtTokenService(new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 }, new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/approvals?status=pending");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data")[0];
        row.GetProperty("student_name").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("admission_no").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("student_class").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("student_section").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("student_roll").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Leave_with_dangling_child_id_returns_null_student_fields()
    {
        var app = MakeApp(fx);
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var danglingChildId = Guid.NewGuid();   // intentionally no Students row

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Asha Parent')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"ChildId\", \"Type\", \"Status\") " +
                "VALUES (@tenantId, @requesterId, @danglingChildId, 'sick', 'pending')",
                new { tenantId, requesterId, danglingChildId });
        }

        var jwt = new JwtTokenService(new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 }, new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/approvals?status=pending");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data")[0];
        row.GetProperty("student_name").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("admission_no").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
