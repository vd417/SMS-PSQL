using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class StaffAttendanceTests(PostgresFixture fx)
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
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["admin"], isPlatform: false);
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

    private static string? FindStatus(JsonElement list, Guid personId) =>
        list.EnumerateArray().First(e => e.GetProperty("person_id").GetGuid() == personId)
            .GetProperty("status").GetString();

    [Fact]
    public async Task Bulk_upsert_is_idempotent_and_updates()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-04-27",
            records = new[]
            {
                new { person_id = t1, status = "present" },
                new { person_id = t2, status = "absent" },
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=teacher&date=2026-04-27"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(2);
        FindStatus(list, t2).Should().Be("absent");

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-04-27",
            records = new[] { new { person_id = t2, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list2 = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=teacher&date=2026-04-27"), HttpStatusCode.OK);
        list2.GetArrayLength().Should().Be(2); // still 2 — upsert, no duplicate
        FindStatus(list2, t2).Should().Be("present");
    }

    [Fact]
    public async Task Teacher_and_staff_marks_for_the_same_person_id_do_not_collide()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TenantClient(app, tenantId);
        var personId = Guid.NewGuid(); // same Guid used as both a teacher id and a staff id

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-04-27",
            records = new[] { new { person_id = personId, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "staff",
            date = "2026-04-27",
            records = new[] { new { person_id = personId, status = "absent" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var teacherList = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=teacher&date=2026-04-27"), HttpStatusCode.OK);
        FindStatus(teacherList, personId).Should().Be("present");

        var staffList = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=staff&date=2026-04-27"), HttpStatusCode.OK);
        FindStatus(staffList, personId).Should().Be("absent");
    }

    [Fact]
    public async Task History_returns_records_across_dates_in_range_only()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TenantClient(app, tenantId);
        var personId = Guid.NewGuid();

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "staff",
            date = "2026-04-27",
            records = new[] { new { person_id = personId, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "staff",
            date = "2026-01-15",
            records = new[] { new { person_id = personId, status = "late" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var history = await Data(
            await client.GetAsync(
                $"/v1/staff-attendance/{personId}?person_type=staff&from=2026-04-01&to=2026-04-30"),
            HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("status").GetString().Should().Be("present");
    }

    [Fact]
    public async Task Range_list_returns_all_people_in_from_to()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TenantClient(app, tenantId);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-08-01",
            records = new[] { new { person_id = a, status = "present" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-08-20",
            records = new[] { new { person_id = b, status = "half_day" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-07-01",
            records = new[] { new { person_id = a, status = "absent" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var range = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=teacher&from=2026-08-01&to=2026-08-31"),
            HttpStatusCode.OK);
        range.GetArrayLength().Should().Be(2);
        FindStatus(range, a).Should().Be("present");
        FindStatus(range, b).Should().Be("half_day");
    }

    [Fact]
    public async Task Invalid_person_type_returns_400()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var res = await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "student",
            date = "2026-04-27",
            records = new[] { new { person_id = Guid.NewGuid(), status = "present" } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_can_mark_half_day_and_it_round_trips()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());
        var personId = Guid.NewGuid();

        (await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-08-26",
            records = new[] { new { person_id = personId, status = "half_day" } }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(
            await client.GetAsync("/v1/staff-attendance?person_type=teacher&date=2026-08-26"),
            HttpStatusCode.OK);
        FindStatus(list, personId).Should().Be("half_day");
    }

    [Fact]
    public async Task Unknown_status_is_rejected()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var res = await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-08-26",
            records = new[] { new { person_id = Guid.NewGuid(), status = "check_in" } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Teacher_cannot_mark_staff_attendance()
    {
        await using var app = App();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), Guid.NewGuid(), ["school.teacher"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.PostAsJsonAsync("/v1/staff-attendance", new
        {
            person_type = "teacher",
            date = "2026-08-26",
            records = new[] { new { person_id = Guid.NewGuid(), status = "present" } }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
