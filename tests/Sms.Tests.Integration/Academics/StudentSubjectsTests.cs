using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class StudentSubjectsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient ClientForUser(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, roles, isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Student_list_returns_only_subjects_mapped_to_their_class()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentUserId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        var mathId = Guid.NewGuid();
        var sciId = Guid.NewGuid();
        var artId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var admin = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.Principal);
        var student = ClientForUser(app, tenantId, studentUserId, Policies.StudentOrParent);

        await using (var conn = new SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status) VALUES (@studentUserId, @tenantId, N'sccrdtb/STU/26/0099', 0, 'active')",
                new { studentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status) " +
                "VALUES (NEWID(), @tenantId, N'sccrdtb/STU/26/0099', N'Ankit', N'9', N'A', N'9-A', N'active')",
                new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name) VALUES (@teacherId, @tenantId, N'Ravi Kumar')",
                new { teacherId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, Subject) VALUES (@classId, @tenantId, N'9-A', N'9', N'A', N'Mathematics')",
                new { classId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Subjects (Id, TenantId, Name, Short, TeacherId) VALUES " +
                "(@mathId, @tenantId, N'Mathematics', N'Math', @teacherId), " +
                "(@sciId, @tenantId, N'Science', N'Sci', NULL), " +
                "(@artId, @tenantId, N'Art', N'Art', NULL)",
                new { mathId, sciId, artId, teacherId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (Id, TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId) " +
                "VALUES (NEWID(), @tenantId, N'Mon', 1, N'Science', @classId, N'9-A', @teacherId)",
                new { tenantId, classId, teacherId });
        }

        var adminList = await Data(await admin.GetAsync("/v1/subjects"), HttpStatusCode.OK);
        adminList.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo("Mathematics", "Science", "Art");

        var studentList = await Data(await student.GetAsync("/v1/subjects"), HttpStatusCode.OK);
        var names = studentList.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        names.Should().BeEquivalentTo("Mathematics", "Science");
        names.Should().NotContain("Art");

        var math = studentList.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Mathematics");
        math.GetProperty("teacher_name").GetString().Should().Be("Ravi Kumar");
        var sci = studentList.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Science");
        sci.GetProperty("teacher_name").GetString().Should().Be("Ravi Kumar");
    }
}
