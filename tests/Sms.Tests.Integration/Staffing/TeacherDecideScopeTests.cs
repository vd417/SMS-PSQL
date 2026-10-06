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
public class TeacherDecideScopeTests(PostgresFixture fx)
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

    private record Seed(Guid TenantId, Guid TeacherUserId, Guid OwnLeave, Guid OtherLeave, Guid StaffLeave);

    private async Task<Seed> SeedAsync(bool teacherHasClass)
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@parentId, @tenantId, 'Asha Parent'), (@teacherUserId, @tenantId, 'Rajesh Teacher'), (@staffUserId, @tenantId, 'Sam Staff')",
            new { parentId, teacherUserId, staffUserId, tenantId });
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
        var own = Guid.NewGuid(); var other = Guid.NewGuid(); var staff = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"LeaveRequests\" (\"Id\", \"TenantId\", \"RequesterId\", \"ChildId\", \"Type\", \"Status\") VALUES " +
            "(@own, @tenantId, @parentId, @s1, 'sick', 'pending'), (@other, @tenantId, @parentId, @s2, 'sick', 'pending')",
            new { own, other, tenantId, parentId, s1, s2 });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"LeaveRequests\" (\"Id\", \"TenantId\", \"RequesterId\", \"Type\", \"Status\") VALUES (@staff, @tenantId, @staffUserId, 'casual', 'pending')",
            new { staff, tenantId, staffUserId });
        return new Seed(tenantId, teacherUserId, own, other, staff);
    }

    private static Task<HttpResponseMessage> Decide(HttpClient c, Guid id) =>
        c.PatchAsJsonAsync($"/v1/approvals/{id}", new { status = "approved" });

    [Fact]
    public async Task Teacher_decides_own_class_student_leave()
    {
        var s = await SeedAsync(teacherHasClass: true);
        var res = await Decide(Client(MakeApp(fx), s.TeacherUserId, s.TenantId, Policies.Teacher), s.OwnLeave);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("approved");
    }

    [Fact]
    public async Task Teacher_cannot_decide_other_class_student_leave()
    {
        var s = await SeedAsync(teacherHasClass: true);
        var res = await Decide(Client(MakeApp(fx), s.TeacherUserId, s.TenantId, Policies.Teacher), s.OtherLeave);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Teacher_cannot_decide_staff_leave()
    {
        var s = await SeedAsync(teacherHasClass: true);
        var res = await Decide(Client(MakeApp(fx), s.TeacherUserId, s.TenantId, Policies.Teacher), s.StaffLeave);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Teacher_without_class_cannot_decide_student_leave()
    {
        var s = await SeedAsync(teacherHasClass: false);
        var res = await Decide(Client(MakeApp(fx), s.TeacherUserId, s.TenantId, Policies.Teacher), s.OwnLeave);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_still_decides_staff_and_student_leave()
    {
        var s = await SeedAsync(teacherHasClass: true);
        var c = Client(MakeApp(fx), Guid.NewGuid(), s.TenantId, Policies.Principal);
        (await Decide(c, s.StaffLeave)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Decide(c, s.OtherLeave)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
