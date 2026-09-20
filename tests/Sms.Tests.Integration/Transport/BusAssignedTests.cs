using System.Net;
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
public class BusAssignedTests(PostgresFixture fx)
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
    public async Task GetAssigned_returns_bus_with_stops_ordered_by_seq()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteName, Driver, DriverPhone) " +
                "VALUES (@Id, @TenantId, @BusNo, @RouteName, @Driver, @DriverPhone)",
                new { Id = busId, TenantId = tenantId, BusNo = "KA-01-F-1234", RouteName = "North Route",
                      Driver = "Ram Kumar", DriverPhone = "9876543210" });

            await conn.ExecuteAsync(
                "INSERT dbo.BusStops (TenantId, BusId, Name, Time, Seq, Lat, Lng) " +
                "VALUES (@TenantId, @BusId, @Name, @Time, @Seq, @Lat, @Lng)",
                new[]
                {
                    new { TenantId = tenantId, BusId = busId, Name = "Stop B", Time = "08:15", Seq = 2, Lat = 12.98, Lng = 77.60 },
                    new { TenantId = tenantId, BusId = busId, Name = "Stop A", Time = "08:00", Seq = 1, Lat = 12.97, Lng = 77.59 }
                });

            await conn.ExecuteAsync(
                "INSERT dbo.BusAssignments (TenantId, TeacherUserId, BusId) " +
                "VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = userId, BusId = busId });
        });

        var client = TeacherClient(app, tenantId, userId);
        var bus = await Data(await client.GetAsync("/v1/bus/assigned"), HttpStatusCode.OK);

        bus.GetProperty("bus_no").GetString().Should().Be("KA-01-F-1234");
        bus.GetProperty("route_name").GetString().Should().Be("North Route");

        var stops = bus.GetProperty("stops");
        stops.GetArrayLength().Should().Be(2);
        stops[0].GetProperty("seq").GetInt32().Should().Be(1);
        stops[1].GetProperty("seq").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetAssigned_returns_404_when_teacher_has_no_assignment()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var client = TeacherClient(app, tenantId, userId);
        var res = await client.GetAsync("/v1/bus/assigned");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAssigned_returns_403_for_student_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var client = StudentParentClient(app, tenantId);
        var res = await client.GetAsync("/v1/bus/assigned");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
