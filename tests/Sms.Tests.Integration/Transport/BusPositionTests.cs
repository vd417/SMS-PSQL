using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusPositionTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TeacherClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [Policies.Teacher], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient StudentParentClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.StudentOrParent], isPlatform: false);
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

    private static async Task Seed(string cs, Guid tenantId, Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task GetPosition_returns_nearest_stop_index_and_progress()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"POS-{Guid.NewGuid():N}"[..12];
        var tripId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        // 3 stops at well-separated lat/lng
        // Stop 1 (Seq=1): lat=12.9700, lng=77.5900
        // Stop 2 (Seq=2): lat=12.9800, lng=77.5900  <- middle
        // Stop 3 (Seq=3): lat=12.9900, lng=77.5900
        // Ping at lat=12.9802, lng=77.5901 — nearest to Stop 2 (index 1)
        var stop1Id = Guid.NewGuid();
        var stop2Id = Guid.NewGuid();
        var stop3Id = Guid.NewGuid();
        const string stop3Name = "Station C";

        const double pingLat = 12.9802;
        const double pingLng = 77.5901;

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });

            await conn.ExecuteAsync(
                "INSERT dbo.BusStops (Id, TenantId, BusId, Name, Seq, Lat, Lng) VALUES (@Id, @TenantId, @BusId, @Name, @Seq, @Lat, @Lng)",
                new object[]
                {
                    new { Id = stop1Id, TenantId = tenantId, BusId = busId, Name = "Station A", Seq = 1, Lat = 12.9700, Lng = 77.5900 },
                    new { Id = stop2Id, TenantId = tenantId, BusId = busId, Name = "Station B", Seq = 2, Lat = 12.9800, Lng = 77.5900 },
                    new { Id = stop3Id, TenantId = tenantId, BusId = busId, Name = stop3Name,   Seq = 3, Lat = 12.9900, Lng = 77.5900 }
                });

            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusId, BusNo, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @BusNo, 'live', @StartedAt)",
                new { Id = tripId, TenantId = tenantId, BusId = busId, BusNo = busNo, StartedAt = DateTime.UtcNow });

            await conn.ExecuteAsync(
                "INSERT dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) VALUES (@Id, @TenantId, @TripId, @Lat, @Lng, @SpeedKmh, @Heading, @At)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, Lat = pingLat, Lng = pingLng, SpeedKmh = 20.0, Heading = 0.0, At = DateTime.UtcNow });
        });

        var client = TeacherClient(app, tenantId, userId);
        var data = await Data(await client.GetAsync($"/v1/bus/{busId}/position"), HttpStatusCode.OK);

        data.GetProperty("current_stop_index").GetInt32().Should().Be(1);
        data.GetProperty("progress").GetDouble().Should().Be(0.5);
        data.GetProperty("next_stop_name").GetString().Should().Be(stop3Name);
        data.GetProperty("lat").GetDouble().Should().Be(pingLat);
        data.GetProperty("lng").GetDouble().Should().Be(pingLng);
        // A real SpeedKmh (20) and a next stop are present, so ETA is now a computed
        // value instead of the old hardcoded null.
        var eta = data.GetProperty("eta_minutes");
        eta.ValueKind.Should().NotBe(JsonValueKind.Null);
        eta.GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetPosition_returns_zeros_when_no_live_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"NPT-{Guid.NewGuid():N}"[..12];

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });
        });

        var client = TeacherClient(app, tenantId, userId);
        var data = await Data(await client.GetAsync($"/v1/bus/{busId}/position"), HttpStatusCode.OK);

        data.GetProperty("current_stop_index").GetInt32().Should().Be(0);
        data.GetProperty("progress").GetDouble().Should().Be(0);
        data.GetProperty("lat").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("lng").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("next_stop_name").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("eta_minutes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetPosition_returns_403_for_student_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        var client = StudentParentClient(app, tenantId);
        var res = await client.GetAsync($"/v1/bus/{busId}/position");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPosition_returns_403_when_plan_lacks_gps()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"GPS-{Guid.NewGuid():N}"[..12];

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });
        });

        var client = TeacherClient(app, tenantId, userId);
        var res = await client.GetAsync($"/v1/bus/{busId}/position");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
