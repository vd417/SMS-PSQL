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
public class PublishAndExtrasTests(PostgresFixture fx)
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
    public async Task Academic_periods_round_trip()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var client = Client(app, tenant, Policies.Principal);

        var empty = await Data(await client.GetAsync("/v1/academic-periods"), HttpStatusCode.OK);
        empty.GetProperty("draft_json").ValueKind.Should().BeOneOf(JsonValueKind.Null, JsonValueKind.Undefined, JsonValueKind.String);

        var draft = "[{\"label\":\"P1\",\"start\":\"08:00\",\"end\":\"08:45\",\"type\":\"Class\"}]";
        var saved = await Data(await client.PutAsJsonAsync("/v1/academic-periods", new
        {
            draft_json = draft,
            published_json = (string?)null,
            draft_saved_at = "2026-08-12T10:00:00Z",
            published_at = (string?)null,
        }), HttpStatusCode.OK);
        saved.GetProperty("draft_json").GetString().Should().Contain("P1");

        var again = await Data(await client.GetAsync("/v1/academic-periods"), HttpStatusCode.OK);
        again.GetProperty("draft_json").GetString().Should().Contain("P1");
    }

    [Fact]
    public async Task Class_tests_round_trip()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var client = Client(app, tenant, Policies.Principal);

        var empty = await Data(await client.GetAsync("/v1/class-tests"), HttpStatusCode.OK);
        empty.GetProperty("draft_json").ValueKind.Should().BeOneOf(JsonValueKind.Null, JsonValueKind.Undefined, JsonValueKind.String);

        var draft = "[{\"id\":1,\"cls\":\"IX-A\",\"subject\":\"Mathematics\",\"title\":\"Unit Test 2\",\"date\":\"2026-06-20\",\"maxMarks\":20}]";
        var saved = await Data(await client.PutAsJsonAsync("/v1/class-tests", new
        {
            draft_json = draft,
            published_json = (string?)null,
            draft_saved_at = "2026-08-13T10:00:00Z",
            published_at = (string?)null,
        }), HttpStatusCode.OK);
        saved.GetProperty("draft_json").GetString().Should().Contain("IX-A");
        saved.GetProperty("draft_json").GetString().Should().Contain("Unit Test 2");

        var again = await Data(await client.GetAsync("/v1/class-tests"), HttpStatusCode.OK);
        again.GetProperty("draft_json").GetString().Should().Contain("Unit Test 2");
    }

    [Fact]
    public async Task Class_tests_save_notifies_student_teacher_and_parent()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var admin = Client(app, tenant, Policies.Principal);
        var teacher = Client(app, tenant, Policies.Teacher);
        var student = Client(app, tenant, Policies.StudentOrParent);
        var parent = Client(app, tenant, Policies.StudentOrParent);

        var draft = "[{\"id\":1,\"cls\":\"IX-A\",\"subject\":\"Mathematics\",\"title\":\"Unit Test 2\",\"date\":\"2026-06-20\",\"maxMarks\":20}]";
        var saved = await Data(await admin.PutAsJsonAsync("/v1/class-tests", new
        {
            draft_json = draft,
            published_json = (string?)null,
            draft_saved_at = "2026-08-13T10:00:00Z",
            published_at = (string?)null,
        }), HttpStatusCode.OK);
        saved.GetProperty("draft_json").GetString().Should().Contain("Unit Test 2");

        await ShouldSeeClassTestNotice(teacher, "Unit Test 2");
        await ShouldSeeClassTestNotice(student, "Unit Test 2");
        await ShouldSeeClassTestNotice(parent, "Unit Test 2");
    }

    private async Task ShouldSeeClassTestNotice(HttpClient client, string title)
    {
        var notifications = await Data(await client.GetAsync("/v1/notifications"), HttpStatusCode.OK);
        var notice = notifications.EnumerateArray()
            .FirstOrDefault(n => n.GetProperty("title").GetString()?.Contains("Class test", StringComparison.OrdinalIgnoreCase) == true);
        notice.ValueKind.Should().NotBe(JsonValueKind.Undefined, "student, teacher, and parent apps all read GET /v1/notifications");
        notice.GetProperty("title").GetString().Should().Contain(title);
    }

    [Fact]
    public async Task Person_extras_student_round_trip()
    {
        await using var app = App();
        var tenant = Guid.NewGuid();
        var client = Client(app, tenant, Policies.Principal);

        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-EX-1",
            name = "Extras Kid",
            grade = "X",
            section = "A",
            roll = 1,
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        var extras = """{"bloodGroup":"O+","files":[{"key":"photo","label":"Photo","fileName":"p.jpg","mime":"image/jpeg","size":12}]}""";
        var put = await Data(await client.PutAsJsonAsync($"/v1/students/{studentId}/extras", new
        {
            extras_json = extras,
        }), HttpStatusCode.OK);
        put.GetProperty("extras_json").GetString().Should().Contain("bloodGroup");

        var get = await Data(await client.GetAsync($"/v1/students/{studentId}/extras"), HttpStatusCode.OK);
        get.GetProperty("extras_json").GetString().Should().Contain("O+");
    }
}
