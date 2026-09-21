using System.Linq;
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

[Collection("sql")]
public class BusConductorAssignmentTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.Principal], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient DriverClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
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
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task UpdateBus_assigns_a_conductor_and_starting_a_trip_sets_ConductorId()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var conductorStaffId = Guid.NewGuid();
        var conductorUserId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        // Bus_Update is gated behind the "operations" plan feature (FeatureCatalog.Operations),
        // so the tenant must be seeded on a tier that has it.
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, @BusNo)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (Id, TenantId, Name, UserId) VALUES (@Id, @TenantId, @Name, @UserId)",
                new { Id = conductorStaffId, TenantId = tenantId, Name = "Priya Rao", UserId = conductorUserId });
        });

        var admin = PrincipalClient(app, tenantId);
        var updated = await Data(await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}",
            new { conductor_staff_id = conductorStaffId }), HttpStatusCode.OK);
        updated.GetProperty("conductor_staff_id").GetGuid().Should().Be(conductorStaffId);

        var driver = DriverClient(app, tenantId, Guid.NewGuid());
        var trip = await Data(await driver.PostAsJsonAsync("/v1/staff/trips",
            new { direction = "pickup", bus_no = busNo }), HttpStatusCode.Created);
        trip.GetProperty("conductor_id").GetGuid().Should().Be(conductorUserId);
    }

    [Fact]
    public async Task AssignTeacher_replaces_the_previous_duty_teacher_on_the_same_bus_instead_of_stacking_rows()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var firstTeacher = Guid.NewGuid();
        var secondTeacher = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-DUP-TEST')",
            new { Id = busId, TenantId = tenantId }));

        var admin = PrincipalClient(app, tenantId);
        await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/teacher", new { teacher_user_id = firstTeacher });
        await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/teacher", new { teacher_user_id = secondTeacher });

        var buses = await Data(await admin.GetAsync("/v1/transport/buses"), HttpStatusCode.OK);
        buses.EnumerateArray().Count(b => b.GetProperty("bus_id").GetGuid() == busId).Should().Be(1);
        buses.EnumerateArray().First(b => b.GetProperty("bus_id").GetGuid() == busId)
            .GetProperty("teacher_user_id").GetGuid().Should().Be(secondTeacher);
    }

    [Fact]
    public async Task ListBuses_reports_the_assigned_conductor_staff_id()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var conductorStaffId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Staff (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = conductorStaffId, TenantId = tenantId, Name = "Reena Gupta" }));

        var admin = PrincipalClient(app, tenantId);
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var created = await Data(await admin.PostAsJsonAsync("/v1/transport/buses",
            new { bus_no = busNo, conductor_staff_id = conductorStaffId }), HttpStatusCode.Created);
        busId = created.GetProperty("bus_id").GetGuid();

        var buses = await Data(await admin.GetAsync("/v1/transport/buses"), HttpStatusCode.OK);
        buses.EnumerateArray().First(b => b.GetProperty("bus_id").GetGuid() == busId)
            .GetProperty("conductor_staff_id").GetGuid().Should().Be(conductorStaffId);
    }

    [Fact]
    public async Task ListBuses_falls_back_to_the_Teachers_row_name_when_the_linked_Users_row_has_no_Name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-NAME-TEST')",
                new { Id = busId, TenantId = tenantId });
            // Users.Name is deliberately left NULL — mirrors real accepted-invite accounts where
            // it's never backfilled, only Email is set.
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email) VALUES (@Id, @TenantId, @Email)",
                new { Id = teacherUserId, TenantId = tenantId, Email = $"noname-{teacherUserId}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@Id, @TenantId, 'Kavya Nair', @UserId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, UserId = teacherUserId });
        });

        var admin = PrincipalClient(app, tenantId);
        await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/teacher", new { teacher_user_id = teacherUserId });

        var buses = await Data(await admin.GetAsync("/v1/transport/buses"), HttpStatusCode.OK);
        buses.EnumerateArray().First(b => b.GetProperty("bus_id").GetGuid() == busId)
            .GetProperty("teacher_name").GetString().Should().Be("Kavya Nair");
    }

    [Fact]
    public async Task CreateBus_succeeds_and_returns_the_conductor_staff_id()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var conductorStaffId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        // Bus_Create is gated behind the "operations" plan feature (FeatureCatalog.Operations),
        // so the tenant must be seeded on a tier that has it.
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Staff (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = conductorStaffId, TenantId = tenantId, Name = "Priya Rao" }));

        var admin = PrincipalClient(app, tenantId);
        var created = await Data(await admin.PostAsJsonAsync("/v1/transport/buses",
            new { bus_no = busNo, conductor_staff_id = conductorStaffId }), HttpStatusCode.Created);

        created.GetProperty("bus_no").GetString().Should().Be(busNo);
        created.GetProperty("conductor_staff_id").GetGuid().Should().Be(conductorStaffId);
        created.GetProperty("status").GetString().Should().Be("idle");
        created.GetProperty("stop_count").GetInt32().Should().Be(0);
        created.GetProperty("students_riding").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetAssignment_returns_the_conductor_name_for_the_driver()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var driverUserId = Guid.NewGuid();
        var driverStaffId = Guid.NewGuid();
        var conductorStaffId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (Id, TenantId, Name, UserId) VALUES (@Id, @TenantId, @Name, @UserId)",
                new[]
                {
                    new { Id = driverStaffId, TenantId = tenantId, Name = "Ram Kumar", UserId = (Guid?)driverUserId },
                    new { Id = conductorStaffId, TenantId = tenantId, Name = "Priya Rao", UserId = (Guid?)null },
                });
            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "North Route" });
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, DriverStaffId, ConductorStaffId) VALUES (@Id, @TenantId, @BusNo, @RouteId, @DriverStaffId, @ConductorStaffId)",
                new { Id = busId, TenantId = tenantId, BusNo = busNo, RouteId = routeId, DriverStaffId = driverStaffId, ConductorStaffId = conductorStaffId });
        });

        var driver = DriverClient(app, tenantId, driverUserId);
        var data = await Data(await driver.GetAsync("/v1/staff/trip/assignment"), HttpStatusCode.OK);
        data.GetProperty("conductor_name").GetString().Should().Be("Priya Rao");
    }
}
