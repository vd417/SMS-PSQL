using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

/// NEW-3 — see docs/superpowers/audits/2026-09-26-sms-api-parity-matrix.md.
[Collection("sql")]
public class BusTeacherAssignmentHttpTests(PostgresFixture fx)
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
    public async Task NEW3_assign_bus_teacher_returns_200_and_persists()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, @BusNo)",
            new { Id = busId, TenantId = tenantId, BusNo = busNo }));

        var principal = Client(app, Guid.NewGuid(), tenantId, Policies.Principal);
        var res = await principal.PutAsJsonAsync(
            $"/v1/transport/buses/{busId}/teacher", new { teacher_user_id = teacherUserId });
        var body = await Data(res, HttpStatusCode.OK);

        body.GetProperty("bus_id").GetGuid().Should().Be(busId);
        body.GetProperty("bus_no").GetString().Should().Be(busNo);
        body.GetProperty("teacher_user_id").GetGuid().Should().Be(teacherUserId);

        // The proc runs before the (previously failing) read, so persistence must be verified
        // independently of the response body.
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        var persisted = await conn.QuerySingleAsync<Guid>(
            "SELECT \"TeacherUserId\" FROM \"dbo\".\"BusAssignments\" WHERE \"BusId\" = @busId", new { busId });
        persisted.Should().Be(teacherUserId);
    }
}
