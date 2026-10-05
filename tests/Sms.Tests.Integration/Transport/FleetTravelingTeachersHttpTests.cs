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

/// B-3 — see docs/superpowers/audits/2026-09-26-sms-api-parity-matrix.md.
[Collection("sql")]
public class FleetTravelingTeachersHttpTests(PostgresFixture fx)
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
    public async Task B3_fleet_includes_traveling_teachers()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-TT-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, @Name)",
                new { Id = teacherUserId, TenantId = tenantId, Name = "Anita Rao" });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusTravelingTeachers\" (\"TenantId\", \"BusId\", \"TeacherUserId\") " +
                "VALUES (@TenantId, @BusId, @TeacherUserId)",
                new { TenantId = tenantId, BusId = busId, TeacherUserId = teacherUserId });
        });

        var principal = Client(app, Guid.NewGuid(), tenantId, Policies.Principal);
        var fleet = await Data(await principal.GetAsync("/v1/transport/fleet"), HttpStatusCode.OK);

        var row = fleet.EnumerateArray().Single(b => b.GetProperty("bus_id").GetGuid() == busId);
        var travelers = row.GetProperty("traveling_teachers");
        travelers.GetArrayLength().Should().Be(1);
        travelers[0].GetProperty("teacher_user_id").GetGuid().Should().Be(teacherUserId);
        travelers[0].GetProperty("teacher_name").GetString().Should().Be("Anita Rao");
    }
}
