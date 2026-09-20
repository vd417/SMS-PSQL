using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ExamsGradesTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), Guid.NewGuid(), [Policies.SchoolAdmin], isPlatform: false);
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

    [Fact]
    public async Task Exam_paper_and_grade_upsert_computes_grade_and_is_idempotent()
    {
        await using var app = App();
        var client = TenantClient(app);

        var examId = (await Data(await client.PostAsJsonAsync("/v1/exams", new
        {
            name = "Term 1", type = "Term", grades = "VI-XII", from_date = "2026-09-01", to_date = "2026-09-15",
            subject_count = 6
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var paperId = (await Data(await client.PostAsJsonAsync("/v1/exam-papers", new
        {
            exam_id = examId, name = "Mathematics", subject = "Mathematics", max_marks = 100, start_time = "09:00",
            duration_min = 180
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var studentId = Guid.NewGuid();
        var g = await Data(await client.PutAsJsonAsync("/v1/grades", new
        {
            student_id = studentId, student_name = "Aarav", exam_paper_id = paperId, marks = 92
        }), HttpStatusCode.OK);
        g.GetProperty("grade").GetString().Should().Be("A1");
        g.GetProperty("gpa").GetDecimal().Should().Be(10);
        g.GetProperty("pass").GetBoolean().Should().BeTrue();

        // re-upsert lower marks -> recomputed, no duplicate
        var g2 = await Data(await client.PutAsJsonAsync("/v1/grades", new
        {
            student_id = studentId, student_name = "Aarav", exam_paper_id = paperId, marks = 20
        }), HttpStatusCode.OK);
        g2.GetProperty("grade").GetString().Should().Be("E");
        g2.GetProperty("pass").GetBoolean().Should().BeFalse();

        var grades = await Data(await client.GetAsync($"/v1/exam-papers/{paperId}/grades"), HttpStatusCode.OK);
        grades.GetArrayLength().Should().Be(1);

        var published = await Data(await client.PatchAsJsonAsync($"/v1/exams/{examId}",
            new { published = true, status = "completed" }), HttpStatusCode.OK);
        published.GetProperty("published").GetBoolean().Should().BeTrue();
    }
}
