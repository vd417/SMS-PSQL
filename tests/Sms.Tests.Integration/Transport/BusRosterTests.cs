using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusRosterTests(PostgresFixture fx)
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

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task GetRoster_returns_two_entries_with_initials_and_status()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var tripId = Guid.NewGuid();
        var student1Id = Guid.NewGuid();
        var student2Id = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            // Bus
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });

            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @AdmissionNo, @Name)",
                new { Id = student1Id, TenantId = tenantId, AdmissionNo = "S001", Name = "Alice Smith" });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @AdmissionNo, @Name)",
                new { Id = student2Id, TenantId = tenantId, AdmissionNo = "S002", Name = "Bob Jones" });

            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new[]
                {
                    new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = student1Id, BusId = busId },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = student2Id, BusId = busId }
                });

            // Live Trip (BusId links to bus; BusNo kept for legacy/back-compat only)
            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusId, BusNo, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @BusNo, 'live', @StartedAt)",
                new { Id = tripId, TenantId = tenantId, BusId = busId, BusNo = busNo, StartedAt = DateTime.UtcNow });

            // Boardings: one boarded, one absent
            await conn.ExecuteAsync(
                "INSERT dbo.Boardings (Id, TenantId, TripId, StudentId, StopId, State, At) " +
                "VALUES (@Id, @TenantId, @TripId, @StudentId, @StopId, @State, @At)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, StudentId = student1Id, StopId = stopId, State = "boarded", At = DateTime.UtcNow });
            await conn.ExecuteAsync(
                "INSERT dbo.Boardings (Id, TenantId, TripId, StudentId, StopId, State, At) " +
                "VALUES (@Id, @TenantId, @TripId, @StudentId, @StopId, @State, @At)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, StudentId = student2Id, StopId = (Guid?)null, State = "absent", At = DateTime.UtcNow });
        });

        var client = TeacherClient(app, tenantId, userId);
        var data = await Data(await client.GetAsync($"/v1/bus/{busId}/roster"), HttpStatusCode.OK);

        data.GetArrayLength().Should().Be(2);

        // ordered by name: Alice Smith before Bob Jones
        var entry0 = data[0];
        entry0.GetProperty("student_name").GetString().Should().Be("Alice Smith");
        entry0.GetProperty("initials").GetString().Should().Be("AS");
        entry0.GetProperty("status").GetString().Should().Be("boarded");

        var entry1 = data[1];
        entry1.GetProperty("student_name").GetString().Should().Be("Bob Jones");
        entry1.GetProperty("initials").GetString().Should().Be("BJ");
        entry1.GetProperty("status").GetString().Should().Be("absent");
    }

    [Fact]
    public async Task GetRoster_returns_empty_when_no_live_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = "NO-TRIP-BUS" });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });
        });

        var client = TeacherClient(app, tenantId, userId);
        var data = await Data(await client.GetAsync($"/v1/bus/{busId}/roster"), HttpStatusCode.OK);

        data.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetRoster_returns_403_for_student_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        var client = StudentParentClient(app, tenantId);
        var res = await client.GetAsync($"/v1/bus/{busId}/roster");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
