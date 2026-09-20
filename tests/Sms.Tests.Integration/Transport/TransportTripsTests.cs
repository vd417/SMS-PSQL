using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TransportTripsTests(PostgresFixture fx)
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

    [Fact]
    public async Task Trip_lifecycle_start_ping_board_end()
    {
        await using var app = App();
        var client = StaffClient(app, Guid.NewGuid(), Guid.NewGuid());

        // start
        var trip = await Data(await client.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = "KA-01-F-2207" }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();
        trip.GetProperty("status").GetString().Should().Be("live");

        // current reflects it
        (await Data(await client.GetAsync("/v1/staff/trip/current"), HttpStatusCode.OK))
            .GetProperty("id").GetGuid().Should().Be(tripId);

        // GPS ping fan-in (TVP) — a short path
        var now = DateTime.UtcNow;
        (await client.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[]
            {
                new { lat = 12.9716, lng = 77.5946, speed_kmh = 0, heading = 0, at = now },
                new { lat = 12.9726, lng = 77.5956, speed_kmh = 30, heading = 45, at = now.AddMinutes(2) },
                new { lat = 12.9736, lng = 77.5966, speed_kmh = 28, heading = 50, at = now.AddMinutes(4) }
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // board a student
        var studentId = Guid.NewGuid();
        (await client.PostAsJsonAsync($"/v1/staff/trips/{tripId}/boarding",
            new { student_id = studentId, state = "boarded", at = now })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var boarding = await Data(await client.GetAsync($"/v1/staff/trips/{tripId}/boarding"), HttpStatusCode.OK);
        boarding.GetArrayLength().Should().Be(1);
        boarding[0].GetProperty("state").GetString().Should().Be("boarded");

        // end -> summary with distance from pings + boarded count
        var summary = await Data(await client.PostAsync($"/v1/staff/trips/{tripId}/end", null), HttpStatusCode.OK);
        summary.GetProperty("distance_km").GetDouble().Should().BeGreaterThan(0);
        summary.GetProperty("boarded_count").GetInt32().Should().Be(1);

        // no longer current
        (await Data(await client.GetAsync("/v1/staff/trip/current"), HttpStatusCode.OK))
            .ValueKind.Should().Be(JsonValueKind.Null);
    }
}
