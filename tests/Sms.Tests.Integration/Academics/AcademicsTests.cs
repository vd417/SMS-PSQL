using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class AcademicsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
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
    public async Task Class_and_subject_create_and_list()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var cls = await Data(await client.PostAsJsonAsync("/v1/classes",
            new { name = "X-A", grade = "X", section = "A", room = "204" }), HttpStatusCode.Created);
        cls.GetProperty("grade").GetString().Should().Be("X");

        var subj = await Data(await client.PostAsJsonAsync("/v1/subjects",
            new { name = "Mathematics", @short = "Math", color = "blue" }), HttpStatusCode.Created);
        subj.GetProperty("name").GetString().Should().Be("Mathematics");

        (await Data(await client.GetAsync("/v1/classes"), HttpStatusCode.OK))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid())
            .Should().Contain(cls.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Roll_call_bulk_upsert_is_idempotent_and_updates()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var classId = (await Data(await client.PostAsJsonAsync("/v1/classes",
            new { name = "IX-B", grade = "IX", section = "B" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var s3 = Guid.NewGuid();

        // first roll-call
        (await client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-04-27",
            records = new[]
            {
                new { student_id = s1, status = "present" },
                new { student_id = s2, status = "absent" },
                new { student_id = s3, status = "late" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(await client.GetAsync($"/v1/classes/{classId}/attendance?date=2026-04-27"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(3);
        FindStatus(list, s2).Should().Be("absent");

        // re-upsert s2 -> present; must update in place, not duplicate
        (await client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-04-27",
            records = new[] { new { student_id = s2, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list2 = await Data(await client.GetAsync($"/v1/classes/{classId}/attendance?date=2026-04-27"), HttpStatusCode.OK);
        list2.GetArrayLength().Should().Be(3); // still 3 — upsert, no duplicate
        FindStatus(list2, s2).Should().Be("present");
    }

    private static string? FindStatus(JsonElement list, Guid studentId) =>
        list.EnumerateArray().First(e => e.GetProperty("student_id").GetGuid() == studentId)
            .GetProperty("status").GetString();

    [Fact]
    public async Task Student_attendance_history_returns_records_across_dates_in_range()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var classId = (await Data(await client.PostAsJsonAsync("/v1/classes",
            new { name = "X-C", grade = "X", section = "C" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var studentId = Guid.NewGuid();

        (await client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-04-27",
            records = new[] { new { student_id = studentId, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-04-28",
            records = new[] { new { student_id = studentId, status = "absent" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var history = await Data(
            await client.GetAsync($"/v1/students/{studentId}/attendance?from=2026-04-01&to=2026-04-30"),
            HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(2);
        history[0].GetProperty("status").GetString().Should().Be("present");
        history[1].GetProperty("status").GetString().Should().Be("absent");
    }

    [Fact]
    public async Task Student_attendance_history_excludes_records_outside_range()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var classId = (await Data(await client.PostAsJsonAsync("/v1/classes",
            new { name = "X-D", grade = "X", section = "D" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var studentId = Guid.NewGuid();

        (await client.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = "2026-01-15",
            records = new[] { new { student_id = studentId, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var history = await Data(
            await client.GetAsync($"/v1/students/{studentId}/attendance?from=2026-04-01&to=2026-04-30"),
            HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(0);
    }
}
