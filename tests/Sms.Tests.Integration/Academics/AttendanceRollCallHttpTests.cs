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
public class AttendanceRollCallHttpTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task Subject_teacher_gets_403_when_not_first_period_or_class_teacher()
    {
        await using var app = App();
        var seed = await SeedRollCallAsync();
        var client = Client(app, seed.SubjectTeacherUserId, seed.TenantId, Policies.Teacher);

        var response = await PostAttendanceAsync(client, seed.ClassId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("not_roll_call_teacher");
    }

    [Fact]
    public async Task First_period_teacher_can_upsert_and_get_shows_can_mark()
    {
        await using var app = App();
        var seed = await SeedRollCallAsync();
        var client = Client(app, seed.FirstPeriodTeacherUserId, seed.TenantId, Policies.Teacher);

        (await PostAttendanceAsync(client, seed.ClassId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.GetAsync(
            $"/v1/classes/{seed.ClassId}/attendance/roll-call?date=2026-08-12");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("period").GetInt32().Should().Be(1);
        data.GetProperty("teacher_name").GetString().Should().Be("First Period Teacher");
        data.GetProperty("class_teacher_name").GetString().Should().Be("Class Teacher");
        data.GetProperty("can_mark").GetBoolean().Should().BeTrue();
        data.GetProperty("reason").GetString().Should().Be("first_period");
        data.GetProperty("marked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Admin_can_always_upsert()
    {
        await using var app = App();
        var seed = await SeedRollCallAsync();
        var client = Client(app, Guid.NewGuid(), seed.TenantId, "admin");

        var response = await PostAttendanceAsync(client, seed.ClassId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static HttpClient Client(
        WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> PostAttendanceAsync(HttpClient client, Guid classId) =>
        client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-08-12",
            records = new[] { new { student_id = Guid.NewGuid(), status = "present" } }
        });

    private async Task<RollCallSeed> SeedRollCallAsync()
    {
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        var classTeacherId = Guid.NewGuid();
        var firstPeriodTeacherId = Guid.NewGuid();
        var subjectTeacherId = Guid.NewGuid();
        var classTeacherUserId = Guid.NewGuid();
        var firstPeriodTeacherUserId = Guid.NewGuid();
        var subjectTeacherUserId = Guid.NewGuid();

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(@"
INSERT dbo.Users (Id, TenantId) VALUES
    (@classTeacherUserId, @tenantId),
    (@firstPeriodTeacherUserId, @tenantId),
    (@subjectTeacherUserId, @tenantId);
INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES
    (@classTeacherId, @tenantId, N'Class Teacher', @classTeacherUserId),
    (@firstPeriodTeacherId, @tenantId, N'First Period Teacher', @firstPeriodTeacherUserId),
    (@subjectTeacherId, @tenantId, N'Subject Teacher', @subjectTeacherUserId);
INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId)
VALUES (@classId, @tenantId, N'IX-A', 0, @classTeacherId);
INSERT dbo.TimetableSlots
    (TenantId, [Day], Period, Subject, ClassId, ClassName, StartTime, EndTime, TeacherId)
VALUES
    (@tenantId, N'Wed', 1, N'Math', @classId, N'IX-A', N'09:00', N'09:45', @firstPeriodTeacherId),
    (@tenantId, N'Wed', 2, N'English', @classId, N'IX-A', N'09:45', N'10:30', @subjectTeacherId);",
            new
            {
                tenantId,
                classId,
                classTeacherId,
                firstPeriodTeacherId,
                subjectTeacherId,
                classTeacherUserId,
                firstPeriodTeacherUserId,
                subjectTeacherUserId,
            });

        return new RollCallSeed(
            tenantId, classId, firstPeriodTeacherUserId, subjectTeacherUserId);
    }

    private sealed record RollCallSeed(
        Guid TenantId,
        Guid ClassId,
        Guid FirstPeriodTeacherUserId,
        Guid SubjectTeacherUserId);
}
