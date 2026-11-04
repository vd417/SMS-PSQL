using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

/// POST /v1/staff/trips: the bus assignment is the source of truth for DriverId/ConductorId;
/// the caller is only the authorized actor (assigned driver or conductor).
[Collection("sql")]
public class StaffTripStartAttributionTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Staff(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", jwt.IssueAccess(userId, tenantId, ["staff"], isPlatform: false));
        return client;
    }

    private static string NewBusNo() => $"KA-{Guid.NewGuid():N}"[..12];

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorCode(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task Driver_starts_and_the_trip_is_attributed_to_the_assigned_driver_and_conductor()
    {
        await using var app = App();
        var (tenantId, driverId, conductorId, busNo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBusNo());
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId, conductorId);

        var trip = await Data(await Staff(app, tenantId, driverId).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);

        trip.GetProperty("driver_id").GetGuid().Should().Be(driverId);
        trip.GetProperty("conductor_id").GetGuid().Should().Be(conductorId);
    }

    [Fact]
    public async Task Conductor_starts_and_the_trip_is_still_attributed_to_the_assigned_driver()
    {
        await using var app = App();
        var (tenantId, driverId, conductorId, busNo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBusNo());
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId, conductorId);

        var trip = await Data(await Staff(app, tenantId, conductorId).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);

        trip.GetProperty("driver_id").GetGuid().Should().Be(driverId, "a conductor must never become driver_id");
        trip.GetProperty("conductor_id").GetGuid().Should().Be(conductorId);
    }

    [Fact]
    public async Task Staff_member_not_assigned_to_the_bus_gets_403_not_assigned()
    {
        await using var app = App();
        var (tenantId, driverId, busNo) = (Guid.NewGuid(), Guid.NewGuid(), NewBusNo());
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);

        var res = await Staff(app, tenantId, Guid.NewGuid()).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(res)).Should().Be("not_assigned");
    }

    [Fact]
    public async Task Bus_with_no_assigned_driver_gets_422_no_driver_assigned()
    {
        await using var app = App();
        var (tenantId, driverId, conductorId, busNo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NewBusNo());
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId, conductorId);
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(conn, "SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE \"dbo\".\"Buses\" SET \"DriverStaffId\" = NULL WHERE \"TenantId\" = @tenantId AND \"BusNo\" = @busNo", new { tenantId, busNo });
        }

        var res = await Staff(app, tenantId, conductorId).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCode(res)).Should().Be("no_driver_assigned");
    }

    [Fact]
    public async Task Unknown_bus_gets_404_bus_not_found()
    {
        await using var app = App();
        var res = await Staff(app, Guid.NewGuid(), Guid.NewGuid()).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = NewBusNo() });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCode(res)).Should().Be("bus_not_found");
    }

    [Fact]
    public async Task Missing_bus_no_gets_422_bus_no_required()
    {
        await using var app = App();
        var res = await Staff(app, Guid.NewGuid(), Guid.NewGuid()).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCode(res)).Should().Be("bus_no_required");
    }

    [Fact]
    public async Task Route_defaults_to_the_buses_route_when_omitted()
    {
        await using var app = App();
        var (tenantId, driverId, busNo, routeId) = (Guid.NewGuid(), Guid.NewGuid(), NewBusNo(), Guid.NewGuid());
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId);
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(conn, "SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE \"dbo\".\"Buses\" SET \"RouteId\" = @routeId WHERE \"TenantId\" = @tenantId AND \"BusNo\" = @busNo", new { routeId, tenantId, busNo });
        }

        var trip = await Data(await Staff(app, tenantId, driverId).PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);

        trip.GetProperty("route_id").GetGuid().Should().Be(routeId);
    }
}
