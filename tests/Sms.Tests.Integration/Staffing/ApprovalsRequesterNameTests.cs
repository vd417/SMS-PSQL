using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class ApprovalsRequesterNameTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Approvals_list_includes_requester_name_from_Users()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Sam Requester')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"Type\", \"Status\") VALUES (@tenantId, @requesterId, 'casual', 'pending')",
                new { tenantId, requesterId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/approvals?status=pending");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("requester_name").GetString().Should().Be("Sam Requester");
    }

    [Fact]
    public async Task Approved_list_includes_decided_by_name_from_Users()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var leaveId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Sam Requester')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@principalId, @tenantId, 'Priya Principal')",
                new { principalId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"Id\", \"TenantId\", \"RequesterId\", \"Type\", \"Status\", \"DecidedBy\", \"DecidedNote\") " +
                "VALUES (@leaveId, @tenantId, @requesterId, 'casual', 'approved', @principalId, 'Covered')",
                new { leaveId, tenantId, requesterId, principalId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(principalId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/approvals?status=approved");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("id").GetGuid().Should().Be(leaveId);
        rows[0].GetProperty("decided_by_name").GetString().Should().Be("Priya Principal");
        rows[0].GetProperty("decided_note").GetString().Should().Be("Covered");
    }

    [Fact]
    public async Task Decide_returns_decided_by_name_of_the_principal()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var leaveId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@requesterId, @tenantId, 'Sam Requester')",
                new { requesterId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@principalId, @tenantId, 'Priya Principal')",
                new { principalId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"Id\", \"TenantId\", \"RequesterId\", \"Type\", \"Status\") " +
                "VALUES (@leaveId, @tenantId, @requesterId, 'casual', 'pending')",
                new { leaveId, tenantId, requesterId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(principalId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.PatchAsJsonAsync($"/v1/approvals/{leaveId}", new { status = "approved", decided_note = "Covered" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data");
        row.GetProperty("status").GetString().Should().Be("approved");
        row.GetProperty("decided_by_name").GetString().Should().Be("Priya Principal");
        row.GetProperty("decided_note").GetString().Should().Be("Covered");
    }
}
