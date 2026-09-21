using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.VehicleChecks;

[Collection("sql")]
public class VehicleCheckEndpointTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> App(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task SeedUserAsync(PostgresFixture fx, Guid tenantId, Guid userId, string name)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@userId, @tenantId, @name)",
            new { userId, tenantId, name });
    }

    private static async Task SeedManagerRoleAsync(PostgresFixture fx, Guid userId, string role)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@userId, @role)", new { userId, role });
    }

    private static async Task<Guid> SeedBusAsync(PostgresFixture fx, Guid tenantId, string busNo)
    {
        var busId = Guid.NewGuid();
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@busId, @tenantId, @busNo)",
            new { busId, tenantId, busNo });
        return busId;
    }

    /// Links userId to busId as its driver or conductor via dbo.Buses.DriverStaffId/
    /// ConductorStaffId (through a dbo.Staff row), the exact assignment concept
    /// TripRepository.IsDriverOrConductorAssignedToBusAsync (and GetAssignmentAsync) resolve.
    private static async Task SeedBusAssignmentAsync(
        PostgresFixture fx, Guid tenantId, Guid busId, Guid userId, string dutyRole)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        // dbo.Staff.UserId is unique, so re-use the same staff row for a userId already linked to
        // one bus (e.g. assigned to two buses in the same test) rather than inserting a second one.
        var staffId = (await conn.QueryFirstOrDefaultAsync<Guid?>(
            "SELECT \"Id\" FROM \"dbo\".\"Staff\" WHERE \"UserId\" = @userId", new { userId })) ?? Guid.NewGuid();
        await conn.ExecuteAsync(
            @"INSERT INTO ""dbo"".""Staff"" (""Id"", ""TenantId"", ""Name"", ""Role"", ""UserId"")
              SELECT @staffId, @tenantId, @name, @dutyRole, @userId
              WHERE NOT EXISTS (SELECT 1 FROM ""dbo"".""Staff"" WHERE ""UserId"" = @userId)",
            new { staffId, tenantId, name = $"Staff {dutyRole}", dutyRole, userId });
        var column = dutyRole == "Driver" ? "DriverStaffId" : "ConductorStaffId";
        await conn.ExecuteAsync($"UPDATE \"dbo\".\"Buses\" SET \"{column}\" = @staffId WHERE \"Id\" = @busId", new { staffId, busId });
    }

    private static object InspectionBody(Guid busId, bool allOk = true, string? remarks = null) => new
    {
        bus_id = busId,
        brakes = true, tyres = true, lights = true, horn = true,
        first_aid_kit = true, fire_extinguisher = true, emergency_exit = true, fuel_level = allOk,
        all_ok = allOk, remarks,
    };

    [Fact]
    public async Task Assigned_driver_can_submit_an_inspection_and_see_it_in_history()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, driverId, "Driver");

        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var create = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busId));
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetAsync($"/v1/staff/vehicle-checks/inspections?busId={busId}");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Submitting_an_inspection_for_a_bus_the_caller_is_not_assigned_to_is_forbidden()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1"); // no assignment for driverId

        var client = ClientFor(App(fx), tenantId, driverId, "driver");
        var create = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busId));
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Submitting_for_another_tenants_bus_is_forbidden_or_not_found()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var driverB = Guid.NewGuid();
        await SeedUserAsync(fx, tenantB, driverB, "Driver B");
        var busA = await SeedBusAsync(fx, tenantA, "BUS-A"); // belongs to tenant A

        var client = ClientFor(App(fx), tenantB, driverB, "driver");
        var create = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busA));
        // RLS hides tenant A's bus from tenant B's session entirely, so the assignment lookup
        // simply finds no match — same 403 an in-tenant unassigned bus gets.
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resubmitting_the_same_bus_same_day_updates_rather_than_duplicates()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, driverId, "Driver");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var first = await client.PostAsJsonAsync(
            "/v1/staff/vehicle-checks/inspections", InspectionBody(busId, remarks: "First submission"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync(
            "/v1/staff/vehicle-checks/inspections", InspectionBody(busId, remarks: "Second submission, same day"));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await client.GetAsync($"/v1/staff/vehicle-checks/inspections?busId={busId}");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(1, "same bus + same day must update in place, never duplicate");
        data[0].GetProperty("remarks").GetString().Should().Be("Second submission, same day");
    }

    [Fact]
    public async Task Failed_inspection_notifies_managers_but_a_passing_one_does_not()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var passingBus = await SeedBusAsync(fx, tenantId, "BUS-PASS");
        var failingBus = await SeedBusAsync(fx, tenantId, "BUS-FAIL");
        await SeedBusAssignmentAsync(fx, tenantId, passingBus, driverId, "Driver");
        await SeedBusAssignmentAsync(fx, tenantId, failingBus, driverId, "Driver");

        var app = App(fx);
        var driverClient = ClientFor(app, tenantId, driverId, "driver");
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);

        var passing = await driverClient.PostAsJsonAsync(
            "/v1/staff/vehicle-checks/inspections", InspectionBody(passingBus, allOk: true));
        passing.StatusCode.Should().Be(HttpStatusCode.OK);

        var failing = await driverClient.PostAsJsonAsync(
            "/v1/staff/vehicle-checks/inspections", InspectionBody(failingBus, allOk: false));
        failing.StatusCode.Should().Be(HttpStatusCode.OK);
        using var failDoc = JsonDocument.Parse(await failing.Content.ReadAsStringAsync());
        failDoc.RootElement.GetProperty("data").GetProperty("all_ok").GetBoolean().Should().BeFalse();

        var notifications = await adminClient.GetAsync("/v1/notifications");
        using var notifDoc = JsonDocument.Parse(await notifications.Content.ReadAsStringAsync());
        var titles = notifDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()).ToList();
        titles.Should().Contain(t => t != null && t.Contains("Vehicle inspection failed"));
        titles.Count(t => t != null && t.Contains("Vehicle inspection failed")).Should()
            .Be(1, "only the failing bus's submission should have notified managers");
    }

    [Fact]
    public async Task Fuel_log_entries_accumulate_as_history_unlike_inspections()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, driverId, "Driver");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var body = new { bus_id = busId, odometer_km = 1000, fuel_added_liters = 20.5 };
        var first = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/fuel-logs", body);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/fuel-logs",
            new { bus_id = busId, odometer_km = 1200, fuel_added_liters = 15.0 });
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetAsync($"/v1/staff/vehicle-checks/fuel-logs?busId={busId}");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(2, "every fuel-up is its own row, never deduplicated");
    }

    [Fact]
    public async Task Non_driver_conductor_role_cannot_submit_an_inspection_or_fuel_log()
    {
        var tenantId = Guid.NewGuid();
        var sweeperId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, sweeperId, "Sweeper One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        var client = ClientFor(App(fx), tenantId, sweeperId, "sweeper");

        var inspection = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busId));
        inspection.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var fuelLog = await client.PostAsJsonAsync(
            "/v1/staff/vehicle-checks/fuel-logs", new { bus_id = busId, odometer_km = 100, fuel_added_liters = 5.0 });
        fuelLog.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_can_view_history_for_any_bus_in_their_tenant_without_being_assigned()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver One");
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, driverId, "Driver");

        var app = App(fx);
        var driverClient = ClientFor(app, tenantId, driverId, "driver");
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);

        var create = await driverClient.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busId));
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await adminClient.GetAsync($"/v1/staff/vehicle-checks/inspections?busId={busId}");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_driver_not_assigned_to_the_bus_cannot_view_its_history()
    {
        var tenantId = Guid.NewGuid();
        var ownerDriverId = Guid.NewGuid();
        var otherDriverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, ownerDriverId, "Owner Driver");
        await SeedUserAsync(fx, tenantId, otherDriverId, "Other Driver");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, ownerDriverId, "Driver");

        var otherClient = ClientFor(App(fx), tenantId, otherDriverId, "driver");
        var list = await otherClient.GetAsync($"/v1/staff/vehicle-checks/inspections?busId={busId}");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Conductor_assignment_is_also_recognized_not_just_driver()
    {
        var tenantId = Guid.NewGuid();
        var conductorId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, conductorId, "Conductor One");
        var busId = await SeedBusAsync(fx, tenantId, "BUS-1");
        await SeedBusAssignmentAsync(fx, tenantId, busId, conductorId, "Conductor");

        var client = ClientFor(App(fx), tenantId, conductorId, "conductor");
        var create = await client.PostAsJsonAsync("/v1/staff/vehicle-checks/inspections", InspectionBody(busId));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
