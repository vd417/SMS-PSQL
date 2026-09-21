using System.Data.SqlTypes;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusCapacityTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [Policies.Principal], isPlatform: false);
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

    private static async Task<Guid> SeedBusAsync(string cs, Guid tenantId, int? capacity)
    {
        var busId = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Buses (Id, TenantId, BusNo, Capacity) VALUES (@Id, @TenantId, @BusNo, @Capacity)",
            new { Id = busId, TenantId = tenantId, BusNo = "CAP-01", Capacity = capacity }));
        return busId;
    }

    private static async Task<Guid> SeedStudentAsync(string cs, Guid tenantId, string name)
    {
        var studentId = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, Name, AdmissionNo) VALUES (@Id, @TenantId, @Name, @AdmissionNo)",
            new { Id = studentId, TenantId = tenantId, Name = name, AdmissionNo = Guid.NewGuid().ToString("N")[..10] }));
        return studentId;
    }

    [Fact]
    public async Task AssignStudent_succeeds_when_under_capacity()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        // Bus assignment is gated behind the "operations" plan feature (FeatureCatalog.Operations),
        // so the tenant must be seeded on a tier that has it.
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = await SeedBusAsync(fx.ConnectionString, tenantId, capacity: 2);
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "Rahul Sharma");

        var client = PrincipalClient(app, tenantId, Guid.NewGuid());
        var res = await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{studentId}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AssignStudent_fails_with_409_when_at_capacity()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = await SeedBusAsync(fx.ConnectionString, tenantId, capacity: 1);
        var seated = await SeedStudentAsync(fx.ConnectionString, tenantId, "Amit Kumar");
        var overflow = await SeedStudentAsync(fx.ConnectionString, tenantId, "Priya Singh");

        var client = PrincipalClient(app, tenantId, Guid.NewGuid());
        (await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{seated}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var res = await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{overflow}", new { });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("capacity_reached");
    }

    [Fact]
    public async Task AssignStudent_succeeds_regardless_of_occupancy_when_capacity_is_null()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = await SeedBusAsync(fx.ConnectionString, tenantId, capacity: null);
        var s1 = await SeedStudentAsync(fx.ConnectionString, tenantId, "Student One");
        var s2 = await SeedStudentAsync(fx.ConnectionString, tenantId, "Student Two");

        var client = PrincipalClient(app, tenantId, Guid.NewGuid());
        (await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{s1}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{s2}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AssignStudent_reassigning_seated_student_to_same_bus_never_false_blocks()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = await SeedBusAsync(fx.ConnectionString, tenantId, capacity: 1);
        var studentId = await SeedStudentAsync(fx.ConnectionString, tenantId, "Seated Student");
        var stopId = Guid.NewGuid();
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq) VALUES (@Id, @TenantId, @RouteId, @Name, 1)",
            new { Id = stopId, TenantId = tenantId, RouteId = Guid.NewGuid(), Name = "Stop A" }));

        var client = PrincipalClient(app, tenantId, Guid.NewGuid());
        (await client.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{studentId}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Re-save the same student on the same (now-full-by-themselves) bus, changing only their stop.
        var res = await client.PutAsJsonAsync(
            $"/v1/transport/buses/{busId}/students/{studentId}", new { stopId });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateBus_with_capacity_returns_the_capacity_via_Bus_Create_proc()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var admin = PrincipalClient(app, tenantId, Guid.NewGuid());
        var res = await admin.PostAsJsonAsync("/v1/transport/buses", new { bus_no = busNo, capacity = 25 });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("capacity").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task UpdateBus_with_clear_capacity_nulls_out_the_capacity_via_Bus_Update_proc()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = await SeedBusAsync(fx.ConnectionString, tenantId, capacity: 40);

        var admin = PrincipalClient(app, tenantId, Guid.NewGuid());
        var res = await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}", new { clear_capacity = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("capacity").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListBusesForRoute_orders_by_id_and_reports_occupancy()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var routeId = Guid.NewGuid();
        Guid busA = Guid.NewGuid(), busB = Guid.NewGuid();
        // Force a deterministic ascending order regardless of generation order. SQL Server's
        // uniqueidentifier ORDER BY does not sort by the same byte order as .NET's Guid.CompareTo,
        // so we compare via SqlGuid (which replicates T-SQL's comparison rules) rather than Guid.CompareTo.
        if (new SqlGuid(busB).CompareTo(new SqlGuid(busA)) < 0) (busA, busB) = (busB, busA);

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "Occupancy Route" });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, Capacity) VALUES (@Id, @TenantId, 'OCC-A', @RouteId, 2)",
                new { Id = busA, TenantId = tenantId, RouteId = routeId });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, Capacity) VALUES (@Id, @TenantId, 'OCC-B', @RouteId, 5)",
                new { Id = busB, TenantId = tenantId, RouteId = routeId });
            var seated = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, 'OCC-S', 'Seated')",
                new { Id = seated, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = seated, B = busA });
        });

        using var scope = App().Services.CreateScope();
        // Resolved directly (not over HTTP) since this is a repository-level query, not an endpoint.
        // ITenantContext must be stamped manually here, as it would be by TenantResolutionMiddleware
        // for a real request, so NpgsqlConnectionFactory sets the session context RLS relies on.
        scope.ServiceProvider.GetRequiredService<Sms.Shared.Kernel.Tenancy.ITenantContext>()
            .Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<Sms.Modules.Transport.BusRepository>();
        var candidates = await repo.ListBusesForRouteAsync(routeId);

        candidates.Should().HaveCount(2);
        candidates.Should().BeInAscendingOrder(c => c.BusId,
            Comparer<Guid>.Create((a, b) => new SqlGuid(a).CompareTo(new SqlGuid(b))));
        candidates.Single(c => c.BusId == busA).Occupied.Should().Be(1);
        candidates.Single(c => c.BusId == busB).Occupied.Should().Be(0);
    }
}
