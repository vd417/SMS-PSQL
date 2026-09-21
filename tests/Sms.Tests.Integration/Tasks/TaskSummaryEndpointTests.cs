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

namespace Sms.Tests.Integration.Tasks;

[Collection("sql")]
public class TaskSummaryEndpointTests(PostgresFixture fx)
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
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Name) VALUES (@userId, @tenantId, @name)",
            new { userId, tenantId, name });
    }

    private static async Task SeedManagerRoleAsync(PostgresFixture fx, Guid userId, string role)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, @role)", new { userId, role });
    }

    private static async Task SeedStaffLinkAsync(
        PostgresFixture fx, Guid tenantId, Guid userId, string name, string designation)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Staff (Id, TenantId, Name, Role, UserId) VALUES (NEWID(), @tenantId, @name, @role, @userId)",
            new { tenantId, name, role = designation, userId });
    }

    // ---- GET /v1/staff/tasks/summary/people ----

    [Fact]
    public async Task People_summary_shows_a_duty_role_staff_member_with_zero_tasks()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, driverId, "Idle Driver");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, driverId, "Idle Driver", "Driver");
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var resp = await client.GetAsync("/v1/staff/tasks/summary/people");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("user_id").GetString() == driverId.ToString());
        row.GetProperty("role_key").GetString().Should().Be("driver");
        row.GetProperty("total_tasks").GetInt32().Should().Be(0);
        row.GetProperty("completed_tasks").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task People_summary_counts_direct_assignment_plus_role_broadcast_for_a_duty_role_staff_member()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, driverId, "Busy Driver");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, driverId, "Busy Driver", "Driver");
        var adminClient = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);
        var driverClient = ClientFor(App(fx), tenantId, driverId, "driver");

        await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Direct", priority = "normal", assigned_to_user_id = driverId });
        var broadcast = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Broadcast", priority = "normal", assigned_to_role_key = "driver" });
        using var broadcastDoc = JsonDocument.Parse(await broadcast.Content.ReadAsStringAsync());
        var broadcastId = broadcastDoc.RootElement.GetProperty("data").GetProperty("id").GetString();
        await driverClient.PostAsync($"/v1/staff/tasks/{broadcastId}/complete", null);

        var resp = await adminClient.GetAsync("/v1/staff/tasks/summary/people");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("user_id").GetString() == driverId.ToString());
        row.GetProperty("total_tasks").GetInt32().Should().Be(2);
        row.GetProperty("pending_tasks").GetInt32().Should().Be(1);
        row.GetProperty("completed_tasks").GetInt32().Should().Be(1);
        row.GetProperty("last_activity_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task People_summary_includes_a_non_duty_role_staff_member_who_was_directly_assigned_a_task()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var librarianId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, librarianId, "Librarian One");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, librarianId, "Librarian One", "Librarian");
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Catalog books", priority = "normal", assigned_to_user_id = librarianId });

        var resp = await client.GetAsync("/v1/staff/tasks/summary/people");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("user_id").GetString() == librarianId.ToString());
        row.GetProperty("role_key").ValueKind.Should().Be(JsonValueKind.Null, "Librarian isn't one of the six duty roles");
        row.GetProperty("total_tasks").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task People_summary_excludes_a_duty_role_staff_member_with_no_linked_login()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (Id, TenantId, Name, Role, UserId) VALUES (NEWID(), @tenantId, N'Unlinked Driver', N'Driver', NULL)",
                new { tenantId });
        }
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var resp = await client.GetAsync("/v1/staff/tasks/summary/people");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").EnumerateArray()
            .Any(r => r.GetProperty("name").GetString() == "Unlinked Driver")
            .Should().BeFalse("a staff row with no linked login can't be identified as a task assignee");
    }

    [Fact]
    public async Task People_summary_is_forbidden_for_a_non_manager()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var resp = await client.GetAsync("/v1/staff/tasks/summary/people");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task People_summary_never_leaks_another_tenants_staff_or_tasks()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var driverA = Guid.NewGuid();
        await SeedUserAsync(fx, tenantA, adminA, "Admin A");
        await SeedUserAsync(fx, tenantA, driverA, "Driver A");
        await SeedUserAsync(fx, tenantB, adminB, "Admin B");
        await SeedManagerRoleAsync(fx, adminA, Policies.SchoolAdmin);
        await SeedManagerRoleAsync(fx, adminB, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantA, driverA, "Driver A", "Driver");
        var app = App(fx);
        var clientB = ClientFor(app, tenantB, adminB, Policies.SchoolAdmin);

        var resp = await clientB.GetAsync("/v1/staff/tasks/summary/people");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").EnumerateArray()
            .Any(r => r.GetProperty("user_id").GetString() == driverA.ToString())
            .Should().BeFalse();
    }

    // ---- GET /v1/staff/tasks/summary/roles ----

    [Fact]
    public async Task Role_summary_always_returns_exactly_the_six_canonical_roles()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var resp = await client.GetAsync("/v1/staff/tasks/summary/roles");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var roleKeys = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("role_key").GetString()).ToList();
        roleKeys.Should().BeEquivalentTo(["driver", "conductor", "sweeper", "gardener", "guard", "peon"]);
    }

    [Fact]
    public async Task Role_summary_headcount_and_totals_reflect_seeded_staff_and_tasks()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var driver1 = Guid.NewGuid();
        var driver2 = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, driver1, "Driver One");
        await SeedUserAsync(fx, tenantId, driver2, "Driver Two");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, driver1, "Driver One", "Driver");
        await SeedStaffLinkAsync(fx, tenantId, driver2, "Driver Two", "Driver");
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Broadcast to drivers", priority = "normal", assigned_to_role_key = "driver" });

        var resp = await client.GetAsync("/v1/staff/tasks/summary/roles");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var driverRow = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("role_key").GetString() == "driver");
        driverRow.GetProperty("headcount").GetInt32().Should().Be(2);
        driverRow.GetProperty("total_tasks").GetInt32().Should().Be(1);
        driverRow.GetProperty("pending_tasks").GetInt32().Should().Be(1);

        var guardRow = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("role_key").GetString() == "guard");
        guardRow.GetProperty("headcount").GetInt32().Should().Be(0);
        guardRow.GetProperty("total_tasks").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Role_summary_is_forbidden_for_a_non_manager()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var resp = await client.GetAsync("/v1/staff/tasks/summary/roles");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Role_summary_headcount_never_counts_another_tenants_staff()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var driverA = Guid.NewGuid();
        await SeedUserAsync(fx, tenantA, adminA, "Admin A");
        await SeedUserAsync(fx, tenantA, driverA, "Driver A");
        await SeedUserAsync(fx, tenantB, adminB, "Admin B");
        await SeedManagerRoleAsync(fx, adminA, Policies.SchoolAdmin);
        await SeedManagerRoleAsync(fx, adminB, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantA, driverA, "Driver A", "Driver");
        var app = App(fx);
        var clientB = ClientFor(app, tenantB, adminB, Policies.SchoolAdmin);

        var resp = await clientB.GetAsync("/v1/staff/tasks/summary/roles");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var driverRow = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("role_key").GetString() == "driver");
        driverRow.GetProperty("headcount").GetInt32().Should().Be(0);
    }

    // ---- SQL role-key mapping mirrors StaffRoleMapper.ToRoleKey exactly ----

    [Theory]
    [InlineData("Driver", "driver")]
    [InlineData("Conductor", "conductor")]
    [InlineData("Bus Attendant", "conductor")]
    [InlineData("Watchman", "guard")]
    [InlineData("Security Guard", "guard")]
    [InlineData("Peon", "peon")]
    [InlineData("Sweeper", "sweeper")]
    [InlineData("Gardener", "gardener")]
    [InlineData("Librarian", null)]
    public async Task People_summary_role_key_matches_StaffRoleMapper_for_every_recognized_designation(
        string designation, string? expectedRoleKey)
    {
        StaffRoleMapper.ToRoleKey(designation).Should().Be(expectedRoleKey, "the C# mapper is the source of truth this test compares SQL against");

        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, staffUserId, $"Test {designation}");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, staffUserId, $"Test {designation}", designation);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        if (expectedRoleKey is null)
        {
            // Not a duty role and has zero tasks, so it's out of People-summary scope entirely —
            // the absence itself is the assertion (see People_summary_includes_a_non_duty_role_
            // staff_member_who_was_directly_assigned_a_task for the "has a task" branch instead).
            var resp = await client.GetAsync("/v1/staff/tasks/summary/people");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("data").EnumerateArray()
                .Any(r => r.GetProperty("user_id").GetString() == staffUserId.ToString())
                .Should().BeFalse();
            return;
        }

        var summary = await client.GetAsync("/v1/staff/tasks/summary/people");
        using var summaryDoc = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        var row = summaryDoc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("user_id").GetString() == staffUserId.ToString());
        row.GetProperty("role_key").GetString().Should().Be(expectedRoleKey);
    }
}
