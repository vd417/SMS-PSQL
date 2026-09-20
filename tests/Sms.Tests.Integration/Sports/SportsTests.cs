using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Sports;

[Collection("sql")]
public class SportsTests(PostgresFixture fx)
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
    public async Task Summary_sums_athletes_and_counts_current_year_medals()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var thisYear = DateTime.UtcNow.Year;

        await Data(await principal.PostAsJsonAsync("/v1/sports/teams",
            new { name = "Senior Football", sport = "Football", coach = "Coach A", athletes = 15 }), HttpStatusCode.Created);
        await Data(await principal.PostAsJsonAsync("/v1/sports/teams",
            new { name = "Junior Cricket", sport = "Cricket", coach = "Coach B", athletes = 10 }), HttpStatusCode.Created);

        await Data(await principal.PostAsJsonAsync("/v1/sports/events",
            new { name = "Annual Meet", event_date = "2026-08-01", venue = "Main Ground" }), HttpStatusCode.Created);

        // one medal this year (counts), one in a prior year (does not count)
        await Data(await principal.PostAsJsonAsync("/v1/sports/medals",
            new { kind = "gold", title = "100m Sprint" }), HttpStatusCode.Created);
        await Data(await principal.PostAsJsonAsync("/v1/sports/medals",
            new { kind = "silver", title = "Old Relay", year = thisYear - 1 }), HttpStatusCode.Created);

        var summary = await Data(await principal.GetAsync("/v1/sports/summary"), HttpStatusCode.OK);
        summary.GetProperty("teams").GetInt32().Should().Be(2);
        summary.GetProperty("events").GetInt32().Should().Be(1);
        summary.GetProperty("athletes").GetInt32().Should().Be(25, "15 + 10 roster");
        summary.GetProperty("medals").GetInt32().Should().Be(1, "only the current-year medal counts");
    }

    [Fact]
    public async Task Invalid_medal_kind_is_rejected()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        (await principal.PostAsJsonAsync("/v1/sports/medals", new { kind = "platinum" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Teacher_is_forbidden()
    {
        await using var app = App();
        var teacher = Client(app, Guid.NewGuid(), Policies.Teacher);

        (await teacher.GetAsync("/v1/sports/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await teacher.PostAsJsonAsync("/v1/sports/teams", new { name = "X", sport = "Y", athletes = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
