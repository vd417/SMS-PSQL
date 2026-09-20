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
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class StudentTransportServiceTests(PostgresFixture fx)
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

    private static async Task Seed(string cs, Guid tenantId, Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    private static async Task<Guid> SeedStudentAsync(string cs, Guid tenantId, string adm)
    {
        var id = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @Adm, 'Test Student')",
            new { Id = id, TenantId = tenantId, Adm = adm }));
        return id;
    }

    private static async Task<Guid> SeedRouteAsync(string cs, Guid tenantId, string name)
    {
        var id = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name }));
        return id;
    }

    private static async Task<Guid> SeedBusOnRouteAsync(string cs, Guid tenantId, Guid routeId, string busNo, int? capacity)
    {
        var id = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, Capacity) VALUES (@Id, @TenantId, @BusNo, @RouteId, @Capacity)",
            new { Id = id, TenantId = tenantId, BusNo = busNo, RouteId = routeId, Capacity = capacity }));
        return id;
    }

    [Fact]
    public async Task Opt_in_with_available_bus_assigns_immediately()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-001");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Avail");
        var busId = await SeedBusOnRouteAsync(fx.ConnectionString, tenantId, routeId, "AVAIL-1", capacity: 2);

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("assigned").GetBoolean().Should().BeTrue();
        data.GetProperty("status").GetString().Should().Be("assigned");
        data.GetProperty("bus_id").GetGuid().Should().Be(busId);
    }

    [Fact]
    public async Task Opt_in_with_no_capacity_saves_as_pending_never_fails()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-002");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Full");
        var busId = await SeedBusOnRouteAsync(fx.ConnectionString, tenantId, routeId, "FULL-1", capacity: 1);
        var seated = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-SEAT");
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
            new { Id = Guid.NewGuid(), TenantId = tenantId, S = seated, B = busId }));

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("assigned").GetBoolean().Should().BeFalse();
        data.GetProperty("status").GetString().Should().Be("pending");
        data.GetProperty("bus_id").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("pending_reason").GetProperty("code").GetString().Should().Be("no_capacity");
    }

    [Fact]
    public async Task Opt_in_without_route_returns_400()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-003");

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Opt_in_with_fee_head_not_flagged_as_transport_returns_400()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-004");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route FH");
        var feeHeadId = Guid.NewGuid();
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.FeeHeads (Id, TenantId, Name, IsTransportFeeHead) VALUES (@Id, @TenantId, 'Tuition', 0)",
            new { Id = feeHeadId, TenantId = tenantId }));

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId, fee_head_id = feeHeadId });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_fee_head");
    }

    [Fact]
    public async Task Opt_out_removes_assignment_row()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-005");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Out");
        var busId = await SeedBusOnRouteAsync(fx.ConnectionString, tenantId, routeId, "OUT-1", capacity: 5);
        var client = AdminClient(app, tenantId);
        await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new { opted_in = true, route_id = routeId });

        var res = await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new { opted_in = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.StudentBusAssignments WHERE StudentId = @studentId", new { studentId });
        count.Should().Be(0);
    }

    [Fact]
    public async Task Re_saving_same_route_keeps_existing_seat_instead_of_evicting_to_pending()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-006");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Resave");
        var busId = await SeedBusOnRouteAsync(fx.ConnectionString, tenantId, routeId, "RESAVE-1", capacity: 1);
        var client = AdminClient(app, tenantId);

        var first = await client.PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId });
        using (var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync()))
        {
            firstDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("assigned");
            firstDoc.RootElement.GetProperty("data").GetProperty("bus_id").GetGuid().Should().Be(busId);
        }

        // Re-save the same route (e.g. changing the stop) now that the sole bus on the route is "full" —
        // with only this student occupying its one seat.
        var stopId = Guid.NewGuid();
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq) VALUES (@Id, @TenantId, @RouteId, 'Resave Stop', 1)",
            new { Id = stopId, TenantId = tenantId, RouteId = routeId }));
        var res = await client.PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId, stop_id = stopId });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("assigned");
        data.GetProperty("assigned").GetBoolean().Should().BeTrue();
        data.GetProperty("bus_id").GetGuid().Should().Be(busId);
    }

    [Fact]
    public async Task Set_transport_returns_403_when_plan_lacks_operations()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-GATE-1");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Gate");

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("feature_locked");
    }

    [Fact]
    public async Task Get_transport_returns_403_when_plan_lacks_operations()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-GATE-2");

        var res = await AdminClient(app, tenantId).GetAsync($"/v1/students/{studentId}/transport");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("feature_locked");
    }

    [Fact]
    public async Task Opt_in_with_bogus_route_returns_404()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-007");

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = Guid.NewGuid() });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task Opt_in_with_bogus_stop_returns_404()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "TS-008");
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Bogus Stop");

        var res = await AdminClient(app, tenantId).PutAsJsonAsync($"/v1/students/{studentId}/transport",
            new { opted_in = true, route_id = routeId, stop_id = Guid.NewGuid() });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("not_found");
    }
}
