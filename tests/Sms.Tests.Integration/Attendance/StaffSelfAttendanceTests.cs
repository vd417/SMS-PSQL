using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Attendance;

/// GET/POST /v1/staff/attendance* — the sms-staff app's own check_in/check_out shape, composed
/// from the same teacher-app attendance service (GetTodayAsync/GetSchoolLocationAsync/
/// PunchAsync) — no new data model. Check-in/out are server-verified from lat/lng, matching the
/// 2026-09-02 design decision (never a client-supplied in_zone flag).
[Collection("sql")]
public class StaffSelfAttendanceTests(PostgresFixture fx)
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

    private static HttpClient StaffClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId) =>
        RoleClient(app, tenantId, userId, "driver");

    /// Setting the school-wide geofence is a principal/admin/owner-only action (a driver must
    /// not be able to overwrite the attendance-verification location for the whole school).
    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId) =>
        RoleClient(app, tenantId, Guid.NewGuid(), "school.admin");

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

    private static async Task SetSchoolLocation(HttpClient client) =>
        (await client.PutAsJsonAsync("/v1/me/attendance/school-location",
            new { lat = SchoolLat, lng = SchoolLng, radius_meters = 50, name = "Main Campus" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Before_any_punch_the_staff_member_is_not_checked_in()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());

        var data = await Data(await client.GetAsync("/v1/staff/attendance"), HttpStatusCode.OK);

        data.GetProperty("checked_in").GetBoolean().Should().BeFalse();
        data.GetProperty("last_log").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Check_in_inside_the_geofence_is_verified()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());
        await SetSchoolLocation(PrincipalClient(app, tenantId));

        var data = await Data(await client.PostAsJsonAsync("/v1/staff/attendance/check-in",
            new { at = DateTime.UtcNow, lat = SchoolLat, lng = SchoolLng, accuracy_meters = 5 }), HttpStatusCode.Created);

        data.GetProperty("checked_in").GetBoolean().Should().BeTrue();
        var log = data.GetProperty("last_log").EnumerateArray().ToList();
        log.Should().ContainSingle();
        log[0].GetProperty("kind").GetString().Should().Be("in");
        log[0].GetProperty("in_zone").GetBoolean().Should().BeTrue();
        data.GetProperty("duty_post").GetString().Should().Be("Main Campus");
        data.GetProperty("geofence_radius_m").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task Check_in_far_from_the_geofence_is_recorded_but_unverified()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());
        await SetSchoolLocation(PrincipalClient(app, tenantId));

        var data = await Data(await client.PostAsJsonAsync("/v1/staff/attendance/check-in",
            new { at = DateTime.UtcNow, lat = SchoolLat + 0.05, lng = SchoolLng, accuracy_meters = 5 }), HttpStatusCode.Created);

        data.GetProperty("checked_in").GetBoolean().Should().BeTrue();
        data.GetProperty("last_log").EnumerateArray().Single().GetProperty("in_zone").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Check_out_after_check_in_ends_the_shift_and_both_appear_in_the_log()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());
        await SetSchoolLocation(PrincipalClient(app, tenantId));

        // Anchored to noon UTC (not DateTime.UtcNow) so check-in/check-out never straddle the
        // UTC day boundary — PunchAsync buckets each punch by the calendar day of its own
        // timestamp, so a run near midnight would otherwise put check-in and check-out in
        // different day-buckets and make this test flaky.
        var shiftEnd = DateTime.UtcNow.Date.AddHours(12);
        await client.PostAsJsonAsync("/v1/staff/attendance/check-in",
            new { at = shiftEnd.AddHours(-1), lat = SchoolLat, lng = SchoolLng, accuracy_meters = 5 });
        var data = await Data(await client.PostAsJsonAsync("/v1/staff/attendance/check-out",
            new { at = shiftEnd, lat = SchoolLat, lng = SchoolLng, accuracy_meters = 5 }), HttpStatusCode.Created);

        data.GetProperty("checked_in").GetBoolean().Should().BeFalse();
        var log = data.GetProperty("last_log").EnumerateArray().ToList();
        log.Should().HaveCount(2);
        log.Select(l => l.GetProperty("kind").GetString()).Should().ContainInOrder("in", "out");
    }

    [Fact]
    public async Task No_school_location_configured_defaults_geofence_fields_honestly()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());

        var data = await Data(await client.GetAsync("/v1/staff/attendance"), HttpStatusCode.OK);

        data.GetProperty("duty_post").GetString().Should().Be("");
        data.GetProperty("geofence_radius_m").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        await using var app = App();
        var client = app.CreateClient();

        (await client.GetAsync("/v1/staff/attendance")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
