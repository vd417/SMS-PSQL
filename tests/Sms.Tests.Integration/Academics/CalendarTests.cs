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
public class CalendarTests(PostgresFixture fx)
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
    public async Task Principal_can_create_event_and_teacher_can_list_it()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var principal = Client(app, tenantId, Policies.Principal);
        var teacher = Client(app, tenantId, Policies.Teacher);

        // POST as principal → 201
        var ev = await Data(await principal.PostAsJsonAsync("/v1/calendar", new
        {
            title = "Sports Day",
            date = "2025-03-15",
            type = "event",
            description = "Annual sports day celebration"
        }), HttpStatusCode.Created);

        ev.GetProperty("title").GetString().Should().Be("Sports Day");
        ev.GetProperty("type").GetString().Should().Be("event");
        ev.GetProperty("description").GetString().Should().Be("Annual sports day celebration");
        var eventId = ev.GetProperty("id").GetGuid();

        // GET as teacher → 200, event appears in list
        var list = await Data(await teacher.GetAsync("/v1/calendar"), HttpStatusCode.OK);
        list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var found = false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == eventId) { found = true; break; }
        }
        found.Should().BeTrue("created event should appear in the teacher's calendar list");
    }

    [Fact]
    public async Task Principal_can_delete_event()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var principal = Client(app, tenantId, Policies.Principal);

        var ev = await Data(await principal.PostAsJsonAsync("/v1/calendar", new
        {
            title = "To Delete",
            date = "2026-08-01",
            type = "holiday",
            description = "gone soon",
            channels_json = "[\"app\",\"email\"]"
        }), HttpStatusCode.Created);
        var eventId = ev.GetProperty("id").GetGuid();

        var del = await principal.DeleteAsync($"/v1/calendar/{eventId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(await principal.GetAsync("/v1/calendar"), HttpStatusCode.OK);
        foreach (var item in list.EnumerateArray())
            item.GetProperty("id").GetGuid().Should().NotBe(eventId);
    }

    [Fact]
    public async Task Teacher_gets_403_on_post()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var postRes = await teacher.PostAsJsonAsync("/v1/calendar", new
        {
            title = "Holiday",
            date = "2025-04-01",
            type = "holiday"
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_get_returns_401()
    {
        await using var app = App();
        var anon = app.CreateClient();
        var res = await anon.GetAsync("/v1/calendar");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task School_admin_and_platform_owner_can_list()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Client(app, tenantId, Policies.SchoolAdmin);
        (await admin.GetAsync("/v1/calendar")).StatusCode.Should().Be(HttpStatusCode.OK);

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var platform = app.CreateClient();
        platform.DefaultRequestHeaders.Authorization =
            new("Bearer", jwt.IssueAccess(Guid.NewGuid(), tenantId, ["owner"], isPlatform: true));
        (await platform.GetAsync("/v1/calendar")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StudentOrParent_can_list_but_gets_403_on_post()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);

        var getRes = await student.GetAsync("/v1/calendar");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var postRes = await student.PostAsJsonAsync("/v1/calendar", new
        {
            title = "Test Event",
            date = "2025-05-01",
            type = "event"
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
