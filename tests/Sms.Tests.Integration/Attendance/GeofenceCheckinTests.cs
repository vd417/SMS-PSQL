using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Attendance;

[Collection("sql")]
public class GeofenceCheckinTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const double SchoolLat = 12.9716;
    private const double SchoolLng = 77.5946;

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TeacherClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId) =>
        RoleClient(app, tenantId, userId, Policies.Teacher);

    /// Setting the school-wide geofence is a principal/admin/owner-only action (a teacher must
    /// not be able to overwrite the attendance-verification location for the whole school).
    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId) =>
        RoleClient(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);

    private static HttpClient RoleClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
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

    private static async Task<JsonElement> Error(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static async Task SetSchoolLocation(HttpClient client) =>
        (await client.PutAsJsonAsync("/v1/me/attendance/school-location", new
        {
            lat = SchoolLat, lng = SchoolLng, radius_meters = 50, name = "Main Campus"
        })).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Punch_inside_geofence_is_verified_and_appears_in_today()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TeacherClient(app, tenantId, Guid.NewGuid());
        await SetSchoolLocation(PrincipalClient(app, tenantId));

        var day = await Data(await client.PostAsJsonAsync("/v1/me/attendance/punch", new
        {
            kind = "in", at = DateTime.UtcNow, lat = SchoolLat, lng = SchoolLng, accuracy_meters = 5
        }), HttpStatusCode.Created);

        var checkIn = day.GetProperty("check_in");
        checkIn.ValueKind.Should().NotBe(JsonValueKind.Null);
        checkIn.GetProperty("verified").GetBoolean().Should().BeTrue();
        checkIn.GetProperty("distance_meters").GetDouble().Should().BeLessThan(50);

        var today = await Data(await client.GetAsync("/v1/me/attendance/today"), HttpStatusCode.OK);
        today.GetProperty("check_in").GetProperty("verified").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Punch_far_from_school_is_saved_as_unverified()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TeacherClient(app, tenantId, Guid.NewGuid());
        await SetSchoolLocation(PrincipalClient(app, tenantId));

        // ~1.1 km away (0.01 degrees latitude)
        var day = await Data(await client.PostAsJsonAsync("/v1/me/attendance/punch", new
        {
            kind = "in", at = DateTime.UtcNow, lat = SchoolLat + 0.01, lng = SchoolLng, accuracy_meters = 5
        }), HttpStatusCode.Created);

        var checkIn = day.GetProperty("check_in");
        checkIn.GetProperty("verified").GetBoolean().Should().BeFalse();
        checkIn.GetProperty("distance_meters").GetDouble().Should().BeGreaterThan(50);

        var today = await Data(await client.GetAsync("/v1/me/attendance/today"), HttpStatusCode.OK);
        today.GetProperty("check_in").GetProperty("verified").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Punch_without_school_location_falls_back_to_manual_on_platinum()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TeacherClient(app, tenantId, Guid.NewGuid());

        var day = await Data(await client.PostAsJsonAsync("/v1/me/attendance/punch", new
        {
            kind = "in", at = DateTime.UtcNow, lat = SchoolLat, lng = SchoolLng, accuracy_meters = 5
        }), HttpStatusCode.Created);

        day.GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Manual_punch_on_gold_plan_succeeds_without_geofence()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");
        var client = TeacherClient(app, tenantId, Guid.NewGuid());

        var day = await Data(await client.PostAsJsonAsync("/v1/me/attendance/punch", new
        {
            kind = "in", at = DateTime.UtcNow, lat = 0.0, lng = 0.0, accuracy_meters = 0
        }), HttpStatusCode.Created);

        day.GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
        var today = await Data(await client.GetAsync("/v1/me/attendance/today"), HttpStatusCode.OK);
        today.GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task School_location_returns_403_when_plan_lacks_geofence()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");
        var client = TeacherClient(app, tenantId, Guid.NewGuid());

        (await client.GetAsync("/v1/me/attendance/school-location")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_can_delete_school_location_and_it_is_gone()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = PrincipalClient(app, tenantId);
        await SetSchoolLocation(principal);

        (await principal.DeleteAsync("/v1/me/attendance/school-location")).StatusCode.Should().Be(HttpStatusCode.OK);

        var err = await Error(await principal.GetAsync("/v1/me/attendance/school-location"), HttpStatusCode.NotFound);
        err.GetProperty("code").GetString().Should().Be("school_location_not_configured");
    }

    [Fact]
    public async Task Teacher_cannot_delete_school_location()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SetSchoolLocation(PrincipalClient(app, tenantId));
        var teacher = TeacherClient(app, tenantId, Guid.NewGuid());

        (await teacher.DeleteAsync("/v1/me/attendance/school-location")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await teacher.GetAsync("/v1/me/attendance/school-location")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
