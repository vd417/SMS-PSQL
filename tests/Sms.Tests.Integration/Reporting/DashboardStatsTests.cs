using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Reporting;

[Collection("sql")]
public class DashboardStatsTests(PostgresFixture fx)
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
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Dashboard_stats_returns_correct_counts_for_teacher()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher, Policies.Principal);

        // Seed 3 students
        var s1 = (await Data(await teacher.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "S001", name = "Alice", grade = "X", section = "A", roll = 1
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s2 = (await Data(await teacher.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "S002", name = "Bob", grade = "X", section = "A", roll = 2
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s3 = (await Data(await teacher.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "S003", name = "Carol", grade = "X", section = "A", roll = 3
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Seed 2 classes
        var classId1 = (await Data(await teacher.PostAsJsonAsync("/v1/classes", new
        {
            name = "X-A", grade = "X", section = "A"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var classId2 = (await Data(await teacher.PostAsJsonAsync("/v1/classes", new
        {
            name = "X-B", grade = "X", section = "B"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Roll-call today: s1=present, s2=late, s3=absent — only present+late count (2)
        var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
        (await teacher.PostAsJsonAsync($"/v1/classes/{classId1}/attendance", new
        {
            date = today,
            records = new[]
            {
                new { student_id = s1, status = "present" },
                new { student_id = s2, status = "late" },
                new { student_id = s3, status = "absent" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Seed 1 homework with status 'todo' (default) and no due date
        (await teacher.PostAsJsonAsync("/v1/homework", new
        {
            student_id = s1, title = "Math Homework"
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        // Seed 1 exam paper with status 'upcoming' — default status for new papers
        (await teacher.PostAsJsonAsync("/v1/exam-papers", new
        {
            name = "Science Exam", max_marks = 100
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        // Call the dashboard stats endpoint
        var stats = await Data(await teacher.GetAsync("/v1/dashboard/stats"), HttpStatusCode.OK);

        stats.GetProperty("total_students").GetInt32().Should().BeGreaterThanOrEqualTo(3);
        stats.GetProperty("total_classes").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        stats.GetProperty("attendance_today").GetInt32().Should().BeGreaterThanOrEqualTo(2);  // present+late only
        stats.GetProperty("pending_assignments").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        stats.GetProperty("upcoming_exams").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Dashboard_stats_returns_403_for_student_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);

        var res = await student.GetAsync("/v1/dashboard/stats");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
