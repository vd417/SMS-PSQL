using System.Linq;
using System.Net;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using System.Text.Json;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

/// B-4 — see docs/superpowers/audits/2026-09-26-sms-api-parity-matrix.md.
[Collection("sql")]
public class BusRouteIdHttpTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
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
    public async Task B4_assigned_bus_includes_route_id()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'North Route')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\") VALUES (@Id, @TenantId, 'BUS-RID-1', @RouteId)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusAssignments\" (\"TenantId\", \"TeacherUserId\", \"BusId\") VALUES (@TenantId, @TeacherUserId, @BusId)",
                new { TenantId = tenantId, TeacherUserId = teacherUserId, BusId = busId });
        });

        var teacher = Client(app, teacherUserId, tenantId, Policies.Teacher);
        var bus = await Data(await teacher.GetAsync("/v1/bus/assigned"), HttpStatusCode.OK);
        bus.GetProperty("route_id").GetGuid().Should().Be(routeId);
    }

    [Fact]
    public async Task B4_traveling_bus_includes_route_id()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'South Route')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\") VALUES (@Id, @TenantId, 'BUS-RID-2', @RouteId)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusTravelingTeachers\" (\"TenantId\", \"BusId\", \"TeacherUserId\") VALUES (@TenantId, @BusId, @TeacherUserId)",
                new { TenantId = tenantId, BusId = busId, TeacherUserId = teacherUserId });
        });

        var teacher = Client(app, teacherUserId, tenantId, Policies.Teacher);
        var buses = await Data(await teacher.GetAsync("/v1/bus/traveling"), HttpStatusCode.OK);
        buses.EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == busId)
            .GetProperty("route_id").GetGuid().Should().Be(routeId);
    }
}
