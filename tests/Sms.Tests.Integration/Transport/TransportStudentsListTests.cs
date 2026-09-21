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
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TransportStudentsListTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.Principal], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task List_returns_mapped_and_pending_with_status_filter()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var routeId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var mappedStudent = Guid.NewGuid();
        var pendingStudent = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\", \"TenantId\", \"Name\") VALUES (@Id, @TenantId, 'List Route')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\", \"RouteId\", \"Capacity\") VALUES (@Id, @TenantId, 'LIST-1', @RouteId, 10)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\") VALUES (@Id, @TenantId, 'M-1', 'Mapped Kid', '5')",
                new { Id = mappedStudent, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\") VALUES (@Id, @TenantId, 'P-1', 'Pending Kid', '6')",
                new { Id = pendingStudent, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\", \"TenantId\", \"StudentId\", \"RouteId\", \"BusId\") VALUES (@Id, @TenantId, @S, @R, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = mappedStudent, R = routeId, B = busId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\", \"TenantId\", \"StudentId\", \"RouteId\", \"BusId\") VALUES (@Id, @TenantId, @S, @R, NULL)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = pendingStudent, R = routeId });
        });

        var client = AdminClient(app, tenantId);
        var all = await client.GetAsync("/v1/transport/students");
        using var allDoc = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        allDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);

        var pendingOnly = await client.GetAsync("/v1/transport/students?status=pending");
        using var pendingDoc = JsonDocument.Parse(await pendingOnly.Content.ReadAsStringAsync());
        var rows = pendingDoc.RootElement.GetProperty("data");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("student_name").GetString().Should().Be("Pending Kid");
        rows[0].GetProperty("mapping_status").GetString().Should().Be("pending");

        // "assigned" is the vocabulary the single-student transport endpoint uses for this same concept —
        // it must behave identically to "mapped" here rather than silently matching nothing.
        var mappedOnly = await client.GetAsync("/v1/transport/students?status=mapped");
        using var mappedDoc = JsonDocument.Parse(await mappedOnly.Content.ReadAsStringAsync());
        var assignedOnly = await client.GetAsync("/v1/transport/students?status=assigned");
        using var assignedDoc = JsonDocument.Parse(await assignedOnly.Content.ReadAsStringAsync());
        assignedDoc.RootElement.GetProperty("data").GetRawText()
            .Should().Be(mappedDoc.RootElement.GetProperty("data").GetRawText());
        assignedDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
        assignedDoc.RootElement.GetProperty("data")[0].GetProperty("student_name").GetString().Should().Be("Mapped Kid");
    }

    [Fact]
    public async Task List_with_unknown_status_returns_400()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AdminClient(app, tenantId);

        var res = await client.GetAsync("/v1/transport/students?status=bogus");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("validation_error");
    }
}
