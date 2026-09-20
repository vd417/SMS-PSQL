using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusBoardingTests(PostgresFixture fx)
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
    public async Task PostBoarding_204_and_roster_shows_boarded_and_absent_statuses()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"BRD-{Guid.NewGuid():N}"[..12];
        var student1Id = Guid.NewGuid();
        var student2Id = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });

            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @AdmissionNo, @Name)",
                new[]
                {
                    new { Id = student1Id, TenantId = tenantId, AdmissionNo = "B001", Name = "Carol Adams" },
                    new { Id = student2Id, TenantId = tenantId, AdmissionNo = "B002", Name = "Dan Brown" }
                });

            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (TenantId, StudentId, BusId) VALUES (@TenantId, @StudentId, @BusId)",
                new[]
                {
                    new { TenantId = tenantId, StudentId = student1Id, BusId = busId },
                    new { TenantId = tenantId, StudentId = student2Id, BusId = busId }
                });

            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusId, BusNo, Status, StartedAt) VALUES (@Id, @TenantId, @BusId, @BusNo, 'live', @StartedAt)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, BusId = busId, BusNo = busNo, StartedAt = DateTime.UtcNow });
        });

        var client = TeacherClient(app, tenantId, userId);

        var payload = new
        {
            records = new[]
            {
                new { student_id = student1Id, stop_id = (Guid?)null, status = "boarded", at = (DateTime?)null },
                new { student_id = student2Id, stop_id = (Guid?)null, status = "absent",  at = (DateTime?)null }
            }
        };

        var postRes = await client.PostAsJsonAsync($"/v1/bus/{busId}/boarding", payload);
        postRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify via GET roster that both entries appear with correct statuses
        var rosterRes = await client.GetAsync($"/v1/bus/{busId}/roster");
        var data = await Data(rosterRes, HttpStatusCode.OK);

        data.GetArrayLength().Should().Be(2);

        // Ordered by name: Carol Adams before Dan Brown
        data[0].GetProperty("student_name").GetString().Should().Be("Carol Adams");
        data[0].GetProperty("status").GetString().Should().Be("boarded");

        data[1].GetProperty("student_name").GetString().Should().Be("Dan Brown");
        data[1].GetProperty("status").GetString().Should().Be("absent");
    }

    [Fact]
    public async Task PostBoarding_returns_409_when_no_live_trip()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var busNo = $"NLT-{Guid.NewGuid():N}"[..12];
        var studentId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });

            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @AdmissionNo, @Name)",
                new { Id = studentId, TenantId = tenantId, AdmissionNo = "B003", Name = "Eve Clark" });
            // No live trip seeded
        });

        var client = TeacherClient(app, tenantId, userId);

        var payload = new
        {
            records = new[] { new { student_id = studentId, stop_id = (Guid?)null, status = "boarded", at = (DateTime?)null } }
        };

        var res = await client.PostAsJsonAsync($"/v1/bus/{busId}/boarding", payload);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("no_active_trip");
    }

    [Fact]
    public async Task PostBoarding_returns_403_for_student_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        var client = StudentParentClient(app, tenantId);

        var payload = new
        {
            records = new[] { new { student_id = Guid.NewGuid(), stop_id = (Guid?)null, status = "boarded", at = (DateTime?)null } }
        };

        var res = await client.PostAsJsonAsync($"/v1/bus/{busId}/boarding", payload);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
