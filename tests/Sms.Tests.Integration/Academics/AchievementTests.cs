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
public class AchievementTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Perfect_attendance_earns_computed_badge()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status) VALUES (@studentId, @tenantId, 'A1', 'S1', 'active')",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Classes (Id, TenantId, Name, StudentCount) VALUES (@classId, @tenantId, 'IV-B', 0)",
                new { classId, tenantId });
            for (var period = 1; period <= 5; period++)
            {
                await conn.ExecuteAsync(@"
INSERT dbo.PeriodAttendanceRecords
  (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status, CreatedAt, UpdatedAt)
VALUES
  (NEWID(), @tenantId, @classId, @studentId, @date, @period, N'Math', N'present', SYSUTCDATETIME(), SYSUTCDATETIME())",
                    new { tenantId, classId, studentId, date = DateTime.UtcNow.Date, period });
            }
        }

        var list = await Data(
            await teacher.GetAsync($"/v1/achievements?student_id={studentId}"), HttpStatusCode.OK);
        list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        list.EnumerateArray().Select(e => e.GetProperty("title").GetString())
            .Should().Contain("Perfect attendance");
    }

    [Fact]
    public async Task Teacher_can_award_and_it_appears_in_list()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status) VALUES (@studentId, @tenantId, 'A2', 'S2', 'active')",
                new { studentId, tenantId });
        }

        var created = await Data(await teacher.PostAsJsonAsync("/v1/achievements", new
        {
            student_id = studentId,
            title = "Science Fair — 1st place",
            awarded_on = "2026-02-14",
            icon = "flag",
            hue = "blue",
        }), HttpStatusCode.Created);
        created.GetProperty("title").GetString().Should().Be("Science Fair — 1st place");
        created.GetProperty("icon").GetString().Should().Be("flag");

        var list = await Data(
            await teacher.GetAsync($"/v1/achievements?student_id={studentId}"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("title").GetString())
            .Should().Contain("Science Fair — 1st place");
    }

    [Fact]
    public async Task Student_cannot_create_an_award()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);
        var res = await student.PostAsJsonAsync("/v1/achievements", new
        {
            student_id = Guid.NewGuid(),
            title = "Fake trophy",
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
