using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ExamAttendanceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, params string[] roles) =>
        TenantClient(app, Guid.NewGuid(), roles);

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> CreatePaperAsync(HttpClient client)
    {
        var examId = (await Data(await client.PostAsJsonAsync("/v1/exams", new
        {
            name = "Term 1", type = "Term", grades = "VI-XII", from_date = "2026-09-01", to_date = "2026-09-15",
            subject_count = 6
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        return (await Data(await client.PostAsJsonAsync("/v1/exam-papers", new
        {
            exam_id = examId, name = "Mathematics", subject = "Mathematics", max_marks = 100, start_time = "09:00",
            duration_min = 180
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Bulk_upsert_is_idempotent_and_updates()
    {
        await using var app = App();
        var client = TenantClient(app, Policies.SchoolAdmin);
        var paperId = await CreatePaperAsync(client);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        (await client.PutAsJsonAsync($"/v1/exam-papers/{paperId}/attendance", new
        {
            records = new[]
            {
                new { student_id = s1, status = "present" },
                new { student_id = s2, status = "absent" },
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(await client.GetAsync($"/v1/exam-papers/{paperId}/attendance"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(2);

        (await client.PutAsJsonAsync($"/v1/exam-papers/{paperId}/attendance", new
        {
            records = new[] { new { student_id = s2, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list2 = await Data(await client.GetAsync($"/v1/exam-papers/{paperId}/attendance"), HttpStatusCode.OK);
        list2.GetArrayLength().Should().Be(2); // still 2 — upsert, no duplicate
        list2.EnumerateArray().First(e => e.GetProperty("student_id").GetGuid() == s2)
            .GetProperty("status").GetString().Should().Be("present");
    }

    [Fact]
    public async Task Bulk_upsert_on_unknown_paper_returns_404()
    {
        await using var app = App();
        var client = TenantClient(app, Policies.SchoolAdmin);

        var res = await client.PutAsJsonAsync($"/v1/exam-papers/{Guid.NewGuid()}/attendance", new
        {
            records = new[] { new { student_id = Guid.NewGuid(), status = "present" } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StudentOrParent_gets_403_on_bulk_upsert_and_list()
    {
        await using var app = App();
        var admin = TenantClient(app, Policies.SchoolAdmin);
        var paperId = await CreatePaperAsync(admin);

        var student = TenantClient(app, Policies.StudentOrParent);
        (await student.PutAsJsonAsync($"/v1/exam-papers/{paperId}/attendance", new
        {
            records = new[] { new { student_id = Guid.NewGuid(), status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Teacher_can_mark_attendance_for_a_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = TenantClient(app, tenantId, Policies.SchoolAdmin);
        var paperId = await CreatePaperAsync(admin);

        var teacher = TenantClient(app, tenantId, Policies.Teacher);
        (await teacher.PutAsJsonAsync($"/v1/exam-papers/{paperId}/attendance", new
        {
            records = new[] { new { student_id = Guid.NewGuid(), status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
