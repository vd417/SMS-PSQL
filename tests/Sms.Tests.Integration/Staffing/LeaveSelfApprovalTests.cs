using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Auth;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class LeaveSelfApprovalTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task A_principal_cannot_decide_their_own_leave()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var principalId = Guid.NewGuid();
        var leaveId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@principalId, @tenantId, 'Priya Principal')",
                new { principalId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"LeaveRequests\" (\"Id\", \"TenantId\", \"RequesterId\", \"Type\", \"Status\") VALUES (@leaveId, @tenantId, @principalId, 'casual', 'pending')",
                new { leaveId, tenantId, principalId });
        }

        var jwt = new JwtTokenService(new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 }, new SystemClock());
        // The caller IS the requester (same user id) and holds Principal.
        var token = jwt.IssueAccess(principalId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.PatchAsJsonAsync($"/v1/approvals/{leaveId}", new { status = "approved" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
