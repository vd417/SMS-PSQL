using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusEtaTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Bus_position_returns_computed_eta_when_speed_available()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@busId, @tenantId, 'B1')", new { busId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@tenantId, @teacherUserId, @busId)",
                new { tenantId, teacherUserId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.BusStops (TenantId, BusId, Name, Seq, Lat, Lng) VALUES (@tenantId, @busId, 'Stop1', 0, 12.9716, 77.5946)",
                new { tenantId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.BusStops (TenantId, BusId, Name, Seq, Lat, Lng) VALUES (@tenantId, @busId, 'Stop2', 1, 12.9816, 77.6046)",
                new { tenantId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusId, Status, StartedAt) VALUES (@tripId, @tenantId, @busId, 'live', SYSUTCDATETIME())",
                new { tripId, tenantId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.TripPings (TenantId, TripId, Lat, Lng, SpeedKmh, At) VALUES (@tenantId, @tripId, 12.9716, 77.5946, 30, SYSUTCDATETIME())",
                new { tenantId, tripId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(teacherUserId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/bus/{busId}/position");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var eta = doc.RootElement.GetProperty("data").GetProperty("eta_minutes");
        eta.ValueKind.Should().NotBe(JsonValueKind.Null);
        eta.GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Bus_position_returns_null_eta_when_speed_missing()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@busId, @tenantId, 'B2')", new { busId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@tenantId, @teacherUserId, @busId)",
                new { tenantId, teacherUserId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.BusStops (TenantId, BusId, Name, Seq, Lat, Lng) VALUES (@tenantId, @busId, 'Stop1', 0, 12.9716, 77.5946)",
                new { tenantId, busId });
            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusId, Status, StartedAt) VALUES (@tripId, @tenantId, @busId, 'live', SYSUTCDATETIME())",
                new { tripId, tenantId, busId });
            // SpeedKmh is NOT NULL on dbo.TripPings (default 0) — 0 IS the "missing" sentinel.
            await conn.ExecuteAsync(
                "INSERT dbo.TripPings (TenantId, TripId, Lat, Lng, SpeedKmh, At) VALUES (@tenantId, @tripId, 12.9716, 77.5946, 0, SYSUTCDATETIME())",
                new { tenantId, tripId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(teacherUserId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/bus/{busId}/position");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("eta_minutes").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
