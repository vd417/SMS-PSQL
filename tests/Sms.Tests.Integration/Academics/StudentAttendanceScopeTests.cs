using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class StudentAttendanceScopeTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string AttendanceDateQuery = "?from=2026-08-12&to=2026-08-12";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Theory]
    [InlineData(Policies.StudentOrParent)]
    [InlineData("student")]
    [InlineData("parent")]
    public async Task Student_or_parent_can_read_only_their_linked_students_attendance(string role)
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.UserId, seed.TenantId, role);

        var otherResponse = await client.GetAsync(
            $"/v1/students/{seed.OtherStudentId}/attendance{AttendanceDateQuery}");

        otherResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var forbidden = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync()))
        {
            forbidden.RootElement.GetProperty("error").GetProperty("code").GetString()
                .Should().Be("not_own_student");
        }

        var ownResponse = await client.GetAsync(
            $"/v1/students/{seed.LinkedStudentId}/attendance{AttendanceDateQuery}");

        ownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var own = JsonDocument.Parse(await ownResponse.Content.ReadAsStringAsync());
        var records = own.RootElement.GetProperty("data").EnumerateArray().ToList();
        records.Should().ContainSingle();
        records[0].GetProperty("student_id").GetGuid().Should().Be(seed.LinkedStudentId);
    }

    [Fact]
    public async Task Unlinked_parent_cannot_read_tenant_student_attendance()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var unlinkedParent = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId",
                new { tenantId = seed.TenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status) VALUES (@id, @tenantId, NULL, 0, N'active');",
                new { id = unlinkedParent, tenantId = seed.TenantId });
        }

        var client = Client(app, unlinkedParent, seed.TenantId, "parent");
        var response = await client.GetAsync(
            $"/v1/students/{seed.OtherStudentId}/attendance{AttendanceDateQuery}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parent_can_read_linked_child_timetable_but_not_another_students()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.UserId, seed.TenantId, "parent");

        var other = await client.GetAsync($"/v1/students/{seed.OtherStudentId}/timetable");
        other.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var own = await client.GetAsync($"/v1/students/{seed.LinkedStudentId}/timetable");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Staff_can_read_any_students_attendance()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, Guid.NewGuid(), seed.TenantId, Policies.Principal);

        var response = await client.GetAsync(
            $"/v1/students/{seed.OtherStudentId}/attendance{AttendanceDateQuery}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient Client(
        WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions
            {
                Issuer = "sms",
                Audience = "sms-apps",
                SigningKey = Key,
                AccessTokenMinutes = 15,
            },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private async Task<AttendanceSeed> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var linkedStudentId = Guid.NewGuid();
        var otherStudentId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        const string admissionNo = "SCOPE/STU/0001";

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId",
            new { tenantId });
        await conn.ExecuteAsync(@"
INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status)
VALUES (@userId, @tenantId, @admissionNo, 0, N'active');
INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status) VALUES
    (@linkedStudentId, @tenantId, @admissionNo, N'Linked Student', N'active'),
    (@otherStudentId, @tenantId, N'SCOPE/STU/0002', N'Other Student', N'active');
INSERT dbo.AttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Status)
VALUES (NEWID(), @tenantId, @classId, @linkedStudentId, '2026-08-12', N'present');
IF OBJECT_ID(N'dbo.ParentStudentLinks', N'U') IS NOT NULL
    INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId)
    VALUES (@userId, @linkedStudentId, @tenantId);",
            new
            {
                tenantId,
                userId,
                admissionNo,
                linkedStudentId,
                otherStudentId,
                classId,
            });

        return new AttendanceSeed(tenantId, userId, linkedStudentId, otherStudentId);
    }

    private sealed record AttendanceSeed(
        Guid TenantId,
        Guid UserId,
        Guid LinkedStudentId,
        Guid OtherStudentId);
}
