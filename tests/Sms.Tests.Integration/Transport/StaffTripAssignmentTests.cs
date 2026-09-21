using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

/// The driver-facing /staff/trip/assignment and /staff/trips/{id}/roster reads. Unlike the
/// admin/teacher /v1/bus surface (keyed off BusAssignments), these are keyed off the driver's
/// own identity via Staff.UserId -> Buses.DriverStaffId, so the Trip screen's very first load
/// can resolve "what am I driving today" without the client supplying any id.
[Collection("sql")]
public class StaffTripAssignmentTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient DriverClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
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

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task GetAssignment_returns_route_bus_and_stops_for_the_driver()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var driverStaffId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (Id, TenantId, Name, Shift, UserId) VALUES (@Id, @TenantId, @Name, '7:00 AM - 4:00 PM', @UserId)",
                new { Id = driverStaffId, TenantId = tenantId, Name = "Ram Kumar", UserId = userId });

            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "North Route" });

            await conn.ExecuteAsync(
                "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, @Lat, @Lng)",
                new[]
                {
                    new { Id = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId, Name = "Stop B", Seq = 2, Lat = 12.98, Lng = 77.60 },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId, Name = "Stop A", Seq = 1, Lat = 12.97, Lng = 77.59 },
                });

            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, DriverStaffId) VALUES (@Id, @TenantId, @BusNo, @RouteId, @DriverStaffId)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo, RouteId = routeId, DriverStaffId = driverStaffId });

            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, BusId, StudentId) VALUES (NEWID(), @TenantId, @BusId, NEWID())",
                new { TenantId = tenantId, BusId = busId });
        });

        var client = DriverClient(app, tenantId, userId);
        var data = await Data(await client.GetAsync("/v1/staff/trip/assignment"), HttpStatusCode.OK);

        data.GetProperty("bus_id").GetGuid().Should().Be(busId);
        data.GetProperty("bus_no").GetString().Should().Be(busNo);
        data.GetProperty("conductor_name").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("shift").GetString().Should().Be("7:00 AM - 4:00 PM");
        data.GetProperty("students_assigned").GetInt32().Should().Be(1);

        var route = data.GetProperty("route");
        route.GetProperty("name").GetString().Should().Be("North Route");
        route.GetProperty("bus_no").GetString().Should().Be(busNo);

        var stops = route.GetProperty("stops");
        stops.GetArrayLength().Should().Be(2);
        stops[0].GetProperty("name").GetString().Should().Be("Stop A");
        stops[0].GetProperty("seq").GetInt32().Should().Be(1);
        stops[1].GetProperty("name").GetString().Should().Be("Stop B");
    }

    [Fact]
    public async Task GetAssignment_returns_404_when_driver_has_no_bus()
    {
        await using var app = App();
        var client = DriverClient(app, Guid.NewGuid(), Guid.NewGuid());
        (await client.GetAsync("/v1/staff/trip/assignment")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRoster_returns_the_bus_students_for_the_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var studentId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @AdmissionNo, @Name)",
                new { Id = studentId, TenantId = tenantId, AdmissionNo = "S001", Name = "Alice Smith" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId, StopId) VALUES (@Id, @TenantId, @StudentId, @BusId, @StopId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = studentId, BusId = busId, StopId = stopId });
        });

        var client = DriverClient(app, tenantId, userId);
        var trip = await Data(await client.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();

        var roster = await Data(await client.GetAsync($"/v1/staff/trips/{tripId}/roster"), HttpStatusCode.OK);
        roster.GetArrayLength().Should().Be(1);
        roster[0].GetProperty("name").GetString().Should().Be("Alice Smith");
        roster[0].GetProperty("stop_id").GetGuid().Should().Be(stopId);
    }

    [Fact]
    public async Task GetRoster_returns_403_for_a_peer_drivers_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var owner = DriverClient(app, tenantId, Guid.NewGuid());
        var peer = DriverClient(app, tenantId, Guid.NewGuid());

        var trip = await Data(await owner.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = "KA-01-F-4501" }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();

        (await peer.GetAsync($"/v1/staff/trips/{tripId}/roster")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
