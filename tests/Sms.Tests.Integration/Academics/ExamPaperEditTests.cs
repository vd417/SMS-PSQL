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
public class ExamPaperEditTests(PostgresFixture fx)
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
    public async Task Patch_updates_changed_field_and_preserves_untouched_field()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        // Seed an exam paper via POST
        var paper = await Data(await teacher.PostAsJsonAsync("/v1/exam-papers", new
        {
            name = "Science Test", subject = "Science", max_marks = 80, start_time = "10:00", duration_min = 120
        }), HttpStatusCode.Created);

        var paperId = paper.GetProperty("id").GetGuid();
        var originalSubject = paper.GetProperty("subject").GetString();
        originalSubject.Should().Be("Science");

        // PATCH only the name; subject should remain unchanged
        var patched = await Data(await teacher.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new
        {
            name = "Science Test (Updated)"
        }), HttpStatusCode.OK);

        patched.GetProperty("name").GetString().Should().Be("Science Test (Updated)");
        patched.GetProperty("subject").GetString().Should().Be("Science"); // untouched field preserved
    }

    [Fact]
    public async Task Delete_returns_204_and_subsequent_get_returns_404()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        // Seed an exam paper via POST
        var paper = await Data(await teacher.PostAsJsonAsync("/v1/exam-papers", new
        {
            name = "History Test", subject = "History", max_marks = 60
        }), HttpStatusCode.Created);

        var paperId = paper.GetProperty("id").GetGuid();

        // DELETE
        var deleteRes = await teacher.DeleteAsync($"/v1/exam-papers/{paperId}");
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Subsequent GET returns 404
        var getRes = await teacher.GetAsync($"/v1/exam-papers/{paperId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Student_cannot_create_exam_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);

        // POST /exam-papers with a student token must be rejected
        var res = await student.PostAsJsonAsync("/v1/exam-papers", new { name = "X", max_marks = 100 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Student_token_gets_403_on_patch()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);
        var student = Client(app, tenantId, Policies.StudentOrParent);

        // Seed a paper as teacher
        var paper = await Data(await teacher.PostAsJsonAsync("/v1/exam-papers", new
        {
            name = "Physics Test", subject = "Physics", max_marks = 100
        }), HttpStatusCode.Created);

        var paperId = paper.GetProperty("id").GetGuid();

        // Student attempts PATCH -> 403
        var res = await student.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Hacked" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
