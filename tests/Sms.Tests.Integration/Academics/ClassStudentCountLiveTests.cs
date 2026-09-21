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

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ClassStudentCountLiveTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task GET_classes_returns_live_student_count_not_stubbed_zero()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"Id\", \"TenantId\", \"Name\", \"Grade\", \"Section\", \"StudentCount\") VALUES (@classId, @tenantId, 'C1', '5', 'A', 0)",
                new { classId, tenantId });
            for (int i = 0; i < 3; i++)
                await conn.ExecuteAsync(
                    "INSERT INTO \"dbo\".\"Students\" (\"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"Status\") VALUES (@tenantId, @adm, @name, '5', 'A', 'active')",
                    new { tenantId, adm = $"A{i}", name = $"Student {i}" });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        // Uses an admin token, not a teacher token: this test verifies live student-count
        // computation, not class-visibility scoping — a teacher token with no class/subject
        // linkage now correctly sees zero classes (see ClassesScopingTests).
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.SchoolAdmin }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/classes");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        var found = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("id").GetGuid() == classId)
            {
                row.GetProperty("student_count").GetInt32().Should().Be(3);
                found = true;
            }
        }
        found.Should().BeTrue();
    }
}
