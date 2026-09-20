using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

/// A driver must only be able to mutate their OWN trip. These tests prove that a second driver in the
/// SAME tenant (so RLS alone does not block them) is rejected with 403 on ping/end/boarding.
[Collection("sql")]
public class TripOwnershipTests(PostgresFixture fx)
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
    public async Task Peer_driver_in_same_tenant_cannot_mutate_anothers_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driver1 = StaffClient(app, tenantId, Guid.NewGuid());
        var driver2 = StaffClient(app, tenantId, Guid.NewGuid());

        // Driver 1 owns the trip.
        var trip = await Data(await driver1.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = "KA-01-F-9001" }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();

        var now = DateTime.UtcNow;

        // Driver 2 (same tenant, different user) is forbidden on every mutation.
        (await driver2.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[] { new { lat = 12.9, lng = 77.5, speed_kmh = 20, heading = 10, at = now } }
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await driver2.PostAsJsonAsync($"/v1/staff/trips/{tripId}/boarding",
            new { student_id = Guid.NewGuid(), state = "boarded", at = now }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await driver2.PostAsync($"/v1/staff/trips/{tripId}/end", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Owner can still end their own trip.
        (await driver1.PostAsync($"/v1/staff/trips/{tripId}/end", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient ConductorClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["conductor"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Assigned_conductor_can_ping_board_and_end_the_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var conductorUserId = Guid.NewGuid();
        var conductorStaffId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (Id, TenantId, Name, UserId) VALUES (@Id, @TenantId, @Name, @UserId)",
                new { Id = conductorStaffId, TenantId = tenantId, Name = "Priya Rao", UserId = conductorUserId });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, ConductorStaffId) VALUES (@Id, @TenantId, @BusNo, @ConductorStaffId)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo, ConductorStaffId = conductorStaffId });
        }

        var driver = StaffClient(app, tenantId, Guid.NewGuid());
        var trip = await Data(await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();

        var conductor = ConductorClient(app, tenantId, conductorUserId);
        var now = DateTime.UtcNow;
        (await conductor.PostAsJsonAsync($"/v1/staff/trips/{tripId}/pings", new
        {
            pings = new[] { new { lat = 12.9, lng = 77.5, speed_kmh = 20, heading = 10, at = now } },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await conductor.PostAsJsonAsync($"/v1/staff/trips/{tripId}/boarding",
            new { student_id = Guid.NewGuid(), state = "boarded", at = now }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await conductor.PostAsync($"/v1/staff/trips/{tripId}/end", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Peer_conductor_not_assigned_to_the_trip_cannot_mutate_it()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driver = StaffClient(app, tenantId, Guid.NewGuid());
        var peerConductor = ConductorClient(app, tenantId, Guid.NewGuid());

        var trip = await Data(await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = "KA-01-F-7701" }), HttpStatusCode.Created);
        var tripId = trip.GetProperty("id").GetGuid();

        (await peerConductor.PostAsync($"/v1/staff/trips/{tripId}/end", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
