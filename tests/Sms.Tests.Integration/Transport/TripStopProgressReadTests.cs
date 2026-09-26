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

[Collection("sql")]
public class TripStopProgressReadTests(PostgresFixture fx)
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

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    /// A live trip started through the real endpoint by the assigned driver, on a route with two
    /// stops, and a ping sitting exactly on Stop A so confirm-arrival passes the radius check.
    private async Task<(Guid tenantId, Guid driverId, Guid conductorId, Guid tripId, Guid stopA, Guid stopB, HttpClient driver)> LiveTrip(WebApplicationFactory<Program> app)
    {
        var (tenantId, driverId, conductorId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (routeId, stopA, stopB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        await TripTestSeed.AssignDriverAsync(fx.ConnectionString, tenantId, busNo, driverId, conductorId);
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""RouteStops"" (""Id"", ""TenantId"", ""RouteId"", ""Name"", ""Seq"", ""Lat"", ""Lng"") VALUES
                  (@A, @T, @R, 'Stop A', 1, 12.1000, 77.1000), (@B, @T, @R, 'Stop B', 2, 12.2000, 77.2000)",
                new { A = stopA, B = stopB, T = tenantId, R = routeId });
        }
        var driver = Staff(app, tenantId, driverId);
        var trip = await Data(await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo, route_id = routeId }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();
        (await driver.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[] { new { lat = 12.1000, lng = 77.1000, speed_kmh = 0, heading = 0, at = DateTime.UtcNow } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        return (tenantId, driverId, conductorId, tripId, stopA, stopB, driver);
    }

    [Fact]
    public async Task Stops_lists_every_route_stop_in_seq_order_with_no_progress_initially()
    {
        await using var app = App();
        var (_, _, _, tripId, stopA, stopB, driver) = await LiveTrip(app);

        var data = await Data(await driver.GetAsync($"/v1/staff/trips/{tripId}/stops"), HttpStatusCode.OK);

        data.GetProperty("trip_id").GetGuid().Should().Be(tripId);
        data.GetProperty("current_stop_id").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("school_arrived_at").ValueKind.Should().Be(JsonValueKind.Null);
        var stops = data.GetProperty("stops");
        stops.GetArrayLength().Should().Be(2);
        stops[0].GetProperty("stop_id").GetGuid().Should().Be(stopA);
        stops[0].GetProperty("seq").GetInt32().Should().Be(1);
        stops[0].GetProperty("confirmed_at").ValueKind.Should().Be(JsonValueKind.Null);
        stops[1].GetProperty("stop_id").GetGuid().Should().Be(stopB);
    }

    [Fact]
    public async Task Confirm_then_complete_is_read_back_and_current_stop_id_tracks_it()
    {
        await using var app = App();
        var (tenantId, _, conductorId, tripId, stopA, _, driver) = await LiveTrip(app);

        (await driver.PostAsync($"/v1/staff/trips/{tripId}/stops/{stopA}/confirm-arrival", null)).IsSuccessStatusCode.Should().BeTrue();
        var afterConfirm = await Data(await driver.GetAsync($"/v1/staff/trips/{tripId}/stops"), HttpStatusCode.OK);
        afterConfirm.GetProperty("current_stop_id").GetGuid().Should().Be(stopA);
        afterConfirm.GetProperty("stops")[0].GetProperty("confirmed_at").ValueKind.Should().Be(JsonValueKind.String);

        var current = await Data(await driver.GetAsync("/v1/staff/trip/current"), HttpStatusCode.OK);
        current.GetProperty("current_stop_id").GetGuid().Should().Be(stopA);

        // The conductor reads the same authoritative state.
        (await Staff(app, tenantId, conductorId).PostAsync($"/v1/staff/trips/{tripId}/stops/{stopA}/complete", null)).IsSuccessStatusCode.Should().BeTrue();
        var afterComplete = await Data(await Staff(app, tenantId, conductorId).GetAsync($"/v1/staff/trips/{tripId}/stops"), HttpStatusCode.OK);
        afterComplete.GetProperty("current_stop_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterComplete.GetProperty("stops")[0].GetProperty("departed_at").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task A_non_participant_in_the_same_tenant_gets_403()
    {
        await using var app = App();
        var (tenantId, _, _, tripId, _, _, _) = await LiveTrip(app);
        (await Staff(app, tenantId, Guid.NewGuid()).GetAsync($"/v1/staff/trips/{tripId}/stops"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_of_another_tenant_cannot_read_the_stops()
    {
        await using var app = App();
        var (_, driverId, _, tripId, _, _, _) = await LiveTrip(app);
        // Same user id, different tenant claim: RLS + the tenant-filtered participant check hide the trip.
        var res = await Staff(app, Guid.NewGuid(), driverId).GetAsync($"/v1/staff/trips/{tripId}/stops");
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }
}
