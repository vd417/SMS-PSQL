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

namespace Sms.Tests.Integration.Hostel;

[Collection("sql")]
public class HostelTests(PostgresFixture fx)
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
    public async Task Summary_reflects_created_blocks_rooms_and_occupancy()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        var block = await Data(await principal.PostAsJsonAsync("/v1/hostel/blocks",
            new { name = "A Block", warden = "Mr. Rao" }), HttpStatusCode.Created);
        var blockId = block.GetProperty("id").GetGuid();

        var room = await Data(await principal.PostAsJsonAsync("/v1/hostel/rooms",
            new { block_id = blockId, room_no = "A-101", capacity = 4 }), HttpStatusCode.Created);
        room.GetProperty("residents").GetInt32().Should().Be(0);
        room.GetProperty("block_name").GetString().Should().Be("A Block");
        var roomId = room.GetProperty("id").GetGuid();

        await Data(await principal.PostAsJsonAsync("/v1/hostel/residents",
            new { room_id = roomId, student_name = "Ravi Kumar" }), HttpStatusCode.Created);

        var summary = await Data(await principal.GetAsync("/v1/hostel/summary"), HttpStatusCode.OK);
        summary.GetProperty("blocks").GetInt32().Should().Be(1);
        summary.GetProperty("rooms").GetInt32().Should().Be(1);
        summary.GetProperty("residents").GetInt32().Should().Be(1);
        summary.GetProperty("occupancy_pct").GetInt32().Should().Be(25, "1 resident of 4 beds = 25%");
    }

    [Fact]
    public async Task Empty_tenant_summary_is_all_zero()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        var summary = await Data(await principal.GetAsync("/v1/hostel/summary"), HttpStatusCode.OK);
        summary.GetProperty("blocks").GetInt32().Should().Be(0);
        summary.GetProperty("rooms").GetInt32().Should().Be(0);
        summary.GetProperty("residents").GetInt32().Should().Be(0);
        summary.GetProperty("occupancy_pct").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Teacher_is_forbidden()
    {
        await using var app = App();
        var teacher = Client(app, Guid.NewGuid(), Policies.Teacher);

        (await teacher.GetAsync("/v1/hostel/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await teacher.PostAsJsonAsync("/v1/hostel/blocks", new { name = "X" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }
}
