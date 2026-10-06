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
public class TeacherApprovalsScopeTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> MakeApp(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 }, new SystemClock());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false));
        return client;
    }

    private async Task<(Guid tenantId, Guid teacherUserId, Guid s1, Guid s2)> SeedAsync(bool teacherHasClass)
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@parentId, @tenantId, 'Asha Parent'), (@teacherUserId, @tenantId, 'Rajesh Teacher')",
            new { parentId, teacherUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@teacherId, @tenantId, 'Rajesh Teacher', @teacherUserId)",
            new { teacherId, tenantId, teacherUserId });
        if (teacherHasClass)
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"TenantId\", \"Name\", \"Grade\", \"Section\", \"ClassTeacherId\") VALUES (@tenantId, 'Grade 5 - A', 'Grade 5', 'A', @teacherId)",
                new { tenantId, teacherId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"ClassLabel\", \"Roll\", \"Status\") " +
            "VALUES (@s1, @tenantId, 'ADM-1', 'Own Class', 'Grade 5', 'A', 'Grade 5 - A', 1, 'active'), " +
            "(@s2, @tenantId, 'ADM-2', 'Other Class', 'Grade 6', 'B', 'Grade 6 - B', 2, 'active')",
            new { s1, s2, tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"ChildId\", \"Type\", \"Status\") VALUES " +
            "(@tenantId, @parentId, @s1, 'sick', 'pending'), (@tenantId, @parentId, @s2, 'sick', 'pending')",
            new { tenantId, parentId, s1, s2 });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"LeaveRequests\" (\"TenantId\", \"RequesterId\", \"Type\", \"Status\") VALUES (@tenantId, @teacherUserId, 'casual', 'pending')",
            new { tenantId, teacherUserId });
        return (tenantId, teacherUserId, s1, s2);
    }

    private static async Task<JsonElement[]> GetRows(HttpClient client)
    {
        var res = await client.GetAsync("/v1/approvals?status=pending");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
    }

    [Fact]
    public async Task Teacher_sees_only_own_class_student_leave()
    {
        var (tenantId, teacherUserId, s1, _) = await SeedAsync(teacherHasClass: true);
        var rows = await GetRows(Client(MakeApp(fx), teacherUserId, tenantId, Policies.Teacher));
        rows.Should().ContainSingle();
        rows[0].GetProperty("child_id").GetGuid().Should().Be(s1);
    }

    [Fact]
    public async Task Teacher_with_no_class_sees_nothing()
    {
        var (tenantId, teacherUserId, _, _) = await SeedAsync(teacherHasClass: false);
        var rows = await GetRows(Client(MakeApp(fx), teacherUserId, tenantId, Policies.Teacher));
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Manager_still_sees_all()
    {
        var (tenantId, _, _, _) = await SeedAsync(teacherHasClass: true);
        var rows = await GetRows(Client(MakeApp(fx), Guid.NewGuid(), tenantId, Policies.Principal));
        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task Teacher_cannot_decide()
    {
        var (tenantId, teacherUserId, _, _) = await SeedAsync(teacherHasClass: true);
        var res = await Client(MakeApp(fx), teacherUserId, tenantId, Policies.Teacher)
            .PatchAsync($"/v1/approvals/{Guid.NewGuid()}", new StringContent("{\"status\":\"approved\"}", System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
