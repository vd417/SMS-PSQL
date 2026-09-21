using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

/// GET /v1/staff/dashboard — hoursThisWeek (real, from CheckIns) + roleCard (real for
/// driver/conductor only, omitted for every other category — see the 2026-09-02 design).
[Collection("sql")]
public class DashboardTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient StaffClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
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

    private async Task<NpgsqlConnection> OpenAsync(Guid tenantId)
    {
        var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    [Fact]
    public async Task No_staff_row_means_no_role_card_but_hours_still_present()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = StaffClient(app, tenantId, Guid.NewGuid());

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);

        data.GetProperty("hours_this_week").GetDouble().Should().Be(0);
        data.TryGetProperty("role_card", out var card).Should().BeTrue();
        card.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Non_transport_category_gets_no_role_card()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        await using (var conn = await OpenAsync(tenantId))
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"Category\", \"UserId\") VALUES (gen_random_uuid(), @TenantId, 'Guard Gopal', 'guard', @UserId)",
                new { TenantId = tenantId, UserId = userId });
        var client = StaffClient(app, tenantId, userId);

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);

        data.GetProperty("role_card").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Driver_with_no_bus_assignment_gets_no_role_card()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        await using (var conn = await OpenAsync(tenantId))
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"Category\", \"UserId\") VALUES (gen_random_uuid(), @TenantId, 'Driver Dan', 'driver', @UserId)",
                new { TenantId = tenantId, UserId = userId });
        var client = StaffClient(app, tenantId, userId);

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);

        data.GetProperty("role_card").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Driver_with_a_bus_gets_a_real_role_card()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        await using (var conn = await OpenAsync(tenantId))
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"Category\", \"Shift\", \"UserId\") VALUES (@Id, @TenantId, 'Driver Dan', 'driver', '7:00 AM - 4:00 PM', @UserId)",
                new { Id = staffId, TenantId = tenantId, UserId = userId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'Route 7')",
                new { Id = routeId, TenantId = tenantId });
            var busId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\", \"DriverStaffId\") VALUES (@BusId, @TenantId, 'KA-01-F-3301', @RouteId, @StaffId)",
                new { BusId = busId, TenantId = tenantId, RouteId = routeId, StaffId = staffId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\", \"TenantId\", \"BusId\", \"StudentId\") VALUES (gen_random_uuid(), @TenantId, @BusId, gen_random_uuid())",
                new { TenantId = tenantId, BusId = busId });
        }
        var client = StaffClient(app, tenantId, userId);

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);
        var card = data.GetProperty("role_card");

        card.GetProperty("kind").GetString().Should().Be("driver");
        card.GetProperty("bus_no").GetString().Should().Be("KA-01-F-3301");
        card.GetProperty("route_name").GetString().Should().Be("Route 7");
        card.GetProperty("shift").GetString().Should().Be("7:00 AM - 4:00 PM");
        card.GetProperty("students_assigned").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Conductor_with_a_bus_gets_a_real_role_card()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        await using (var conn = await OpenAsync(tenantId))
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"Category\", \"UserId\") VALUES (@Id, @TenantId, 'Conductor Cathy', 'conductor', @UserId)",
                new { Id = staffId, TenantId = tenantId, UserId = userId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'Route 9')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\", \"ConductorStaffId\") VALUES (gen_random_uuid(), @TenantId, 'KA-02-G-1180', @RouteId, @StaffId)",
                new { TenantId = tenantId, RouteId = routeId, StaffId = staffId });
        }
        var client = StaffClient(app, tenantId, userId);

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);
        var card = data.GetProperty("role_card");

        card.GetProperty("kind").GetString().Should().Be("conductor");
        card.GetProperty("bus_no").GetString().Should().Be("KA-02-G-1180");
        card.GetProperty("route_name").GetString().Should().Be("Route 9");
    }

    [Fact]
    public async Task Hours_this_week_reflects_a_completed_punch_pair()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        var client = StaffClient(app, tenantId, userId);

        // Anchored to noon UTC (not DateTime.UtcNow) so check-in/check-out never straddle the
        // UTC day boundary — hours-per-day pairing groups punches by the calendar day of their
        // own timestamp, so a run near midnight would otherwise split this pair across two days
        // and make this test flaky.
        var now = DateTime.UtcNow.Date.AddHours(12);
        (await client.PostAsJsonAsync("/v1/staff/attendance/check-in",
            new { at = now.AddHours(-3), lat = 0.0, lng = 0.0, accuracy_meters = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/v1/staff/attendance/check-out",
            new { at = now, lat = 0.0, lng = 0.0, accuracy_meters = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var data = await Data(await client.GetAsync("/v1/staff/dashboard"), HttpStatusCode.OK);

        data.GetProperty("hours_this_week").GetDouble().Should().BeApproximately(3, 0.05);
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        await using var app = App();
        var client = app.CreateClient();

        (await client.GetAsync("/v1/staff/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
