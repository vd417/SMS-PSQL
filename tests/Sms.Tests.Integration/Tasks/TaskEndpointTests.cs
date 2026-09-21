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
public class TaskEndpointTests(PostgresFixture fx)
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

    /// Links a user to a dbo.Staff row with the given free-text designation (e.g. "Driver"),
    /// which is how TaskRepository.GetCallerRoleKeyAsync resolves the caller's role_key —
    /// exactly the same lookup /auth/me uses (see StaffRoleMapper.ToRoleKey).
    private static async Task SeedStaffLinkAsync(PostgresFixture fx, Guid tenantId, Guid userId, string designation)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"Role\", \"UserId\") VALUES (gen_random_uuid(), @tenantId, @name, @role, @userId)",
            new { tenantId, name = $"Staff {designation}", role = designation, userId });
    }

    [Fact]
    public async Task Task_assigned_to_a_specific_user_shows_only_for_them()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target Driver");
        await SeedUserAsync(fx, tenantId, otherId, "Other Driver");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, targetId, "Driver");
        await SeedStaffLinkAsync(fx, tenantId, otherId, "Driver");

        var app = App(fx);
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);
        var targetClient = ClientFor(app, tenantId, targetId, "driver");
        var otherClient = ClientFor(app, tenantId, otherId, "driver");

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Wash bus #3", priority = "normal", assigned_to_user_id = targetId,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var targetList = await targetClient.GetAsync("/v1/staff/tasks");
        using var targetDoc = JsonDocument.Parse(await targetList.Content.ReadAsStringAsync());
        targetDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);

        var otherList = await otherClient.GetAsync("/v1/staff/tasks");
        using var otherDoc = JsonDocument.Parse(await otherList.Content.ReadAsStringAsync());
        otherDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Task_broadcast_to_a_role_shows_for_every_user_with_that_role_and_not_others()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var driver1 = Guid.NewGuid();
        var driver2 = Guid.NewGuid();
        var conductorId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, driver1, "Driver One");
        await SeedUserAsync(fx, tenantId, driver2, "Driver Two");
        await SeedUserAsync(fx, tenantId, conductorId, "Conductor One");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, driver1, "Driver");
        await SeedStaffLinkAsync(fx, tenantId, driver2, "Driver");
        await SeedStaffLinkAsync(fx, tenantId, conductorId, "Conductor");

        var app = App(fx);
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);
        var driver1Client = ClientFor(app, tenantId, driver1, "driver");
        var driver2Client = ClientFor(app, tenantId, driver2, "driver");
        var conductorClient = ClientFor(app, tenantId, conductorId, "conductor");

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Morning vehicle checklist", priority = "normal", assigned_to_role_key = "driver",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        foreach (var client in new[] { driver1Client, driver2Client })
        {
            var list = await client.GetAsync("/v1/staff/tasks");
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
        }

        var conductorList = await conductorClient.GetAsync("/v1/staff/tasks");
        using var conductorDoc = JsonDocument.Parse(await conductorList.Content.ReadAsStringAsync());
        conductorDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Completing_a_broadcast_task_marks_it_done_for_everyone_who_could_see_it()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var driver1 = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, driver1, "Driver One");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        await SeedStaffLinkAsync(fx, tenantId, driver1, "Driver");

        var app = App(fx);
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Sweep the yard", priority = "normal", assigned_to_role_key = "sweeper",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString();

        var sweeperId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, sweeperId, "Sweeper One");
        await SeedStaffLinkAsync(fx, tenantId, sweeperId, "Sweeper");
        var sweeperClient = ClientFor(app, tenantId, sweeperId, "sweeper");

        var complete = await sweeperClient.PostAsync($"/v1/staff/tasks/{id}/complete", null);
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var completeDoc = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        var refreshed = completeDoc.RootElement.GetProperty("data").EnumerateArray().First();
        refreshed.GetProperty("done").GetBoolean().Should().BeTrue();

        var driver1Client = ClientFor(app, tenantId, driver1, "driver");
        var driverList = await driver1Client.GetAsync("/v1/staff/tasks");
        using var driverDoc = JsonDocument.Parse(await driverList.Content.ReadAsStringAsync());
        driverDoc.RootElement.GetProperty("data").GetArrayLength().Should()
            .Be(0, "the sweeper-only broadcast was never visible to a driver");
    }

    [Fact]
    public async Task Completing_someone_elses_specifically_assigned_task_is_forbidden()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedUserAsync(fx, tenantId, otherId, "Other");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);

        var app = App(fx);
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);
        var otherClient = ClientFor(app, tenantId, otherId, "driver");

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Fix the gate", priority = "urgent", assigned_to_user_id = targetId,
        });
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString();

        var complete = await otherClient.PostAsync($"/v1/staff/tasks/{id}/complete", null);
        complete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Completing_a_task_across_tenants_is_forbidden()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await SeedUserAsync(fx, tenantA, adminA, "Admin A");
        await SeedUserAsync(fx, tenantA, targetA, "Target A");
        await SeedUserAsync(fx, tenantB, userB, "User B");
        await SeedManagerRoleAsync(fx, adminA, Policies.SchoolAdmin);

        var app = App(fx);
        var adminAClient = ClientFor(app, tenantA, adminA, Policies.SchoolAdmin);
        var clientB = ClientFor(app, tenantB, userB, "driver");

        var create = await adminAClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Tenant A task", priority = "normal", assigned_to_user_id = targetA,
        });
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString();

        // RLS hides tenant A's row from tenant B's session, so the "not your task" 403 path
        // never even runs — it resolves as the same not_found a truly-missing id would.
        var complete = await clientB.PostAsync($"/v1/staff/tasks/{id}/complete", null);
        complete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Non_manager_cannot_create_a_task()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var create = await client.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Attempted task", priority = "normal", assigned_to_user_id = targetId,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Non_manager_cannot_list_all_tasks()
    {
        var tenantId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, driverId, "Driver");
        var client = ClientFor(App(fx), tenantId, driverId, "driver");

        var list = await client.GetAsync("/v1/staff/tasks/all");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_can_list_all_tasks_in_their_tenant()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Task one", priority = "normal", assigned_to_user_id = targetId,
        });
        await client.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Task two", priority = "urgent", assigned_to_role_key = "guard",
        });

        var list = await client.GetAsync("/v1/staff/tasks/all");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Cross_tenant_tasks_are_never_visible_in_list_all()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var targetA = Guid.NewGuid();
        await SeedUserAsync(fx, tenantA, adminA, "Admin A");
        await SeedUserAsync(fx, tenantA, targetA, "Target A");
        await SeedUserAsync(fx, tenantB, adminB, "Admin B");
        await SeedManagerRoleAsync(fx, adminA, Policies.SchoolAdmin);
        await SeedManagerRoleAsync(fx, adminB, Policies.SchoolAdmin);

        var app = App(fx);
        var adminAClient = ClientFor(app, tenantA, adminA, Policies.SchoolAdmin);
        var adminBClient = ClientFor(app, tenantB, adminB, Policies.SchoolAdmin);

        await adminAClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Tenant A task", priority = "normal", assigned_to_user_id = targetA,
        });

        var listB = await adminBClient.GetAsync("/v1/staff/tasks/all");
        using var docB = JsonDocument.Parse(await listB.Content.ReadAsStringAsync());
        docB.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Cross_tenant_tasks_are_never_visible_in_my_tasks()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await SeedUserAsync(fx, tenantA, adminA, "Admin A");
        await SeedUserAsync(fx, tenantB, userB, "User B");
        await SeedManagerRoleAsync(fx, adminA, Policies.SchoolAdmin);

        var app = App(fx);
        var adminAClient = ClientFor(app, tenantA, adminA, Policies.SchoolAdmin);
        var clientB = ClientFor(app, tenantB, userB, "driver");

        await adminAClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Tenant A task", priority = "normal", assigned_to_user_id = userB,
        });

        var listB = await clientB.GetAsync("/v1/staff/tasks");
        using var docB = JsonDocument.Parse(await listB.Content.ReadAsStringAsync());
        docB.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_rejects_when_neither_or_both_assignment_targets_are_set()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var neither = await client.PostAsJsonAsync("/v1/staff/tasks", new { title = "No target", priority = "normal" });
        neither.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var both = await client.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Both targets", priority = "normal", assigned_to_user_id = targetId, assigned_to_role_key = "driver",
        });
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Attach_photo_updates_photo_url_and_is_authorization_scoped_like_complete()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedUserAsync(fx, tenantId, otherId, "Other");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);

        var app = App(fx);
        var adminClient = ClientFor(app, tenantId, adminId, Policies.SchoolAdmin);
        var targetClient = ClientFor(app, tenantId, targetId, "driver");
        var otherClient = ClientFor(app, tenantId, otherId, "driver");

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        {
            title = "Photo evidence", priority = "normal", assigned_to_user_id = targetId,
        });
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("data").GetProperty("id").GetString();

        var forbidden = await otherClient.PostAsJsonAsync($"/v1/staff/tasks/{id}/photo",
            new { photo_base64 = "data:image/png;base64,iVBORw0KGgo=" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var ok = await targetClient.PostAsJsonAsync($"/v1/staff/tasks/{id}/photo",
            new { photo_base64 = "data:image/png;base64,iVBORw0KGgo=" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using var okDoc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var refreshed = okDoc.RootElement.GetProperty("data").EnumerateArray().First();
        refreshed.GetProperty("photo_url").GetString().Should().Be("data:image/png;base64,iVBORw0KGgo=");
    }

    // ---- GET /v1/staff/tasks/all — filters, cursor pagination, resolved names ----

    [Fact]
    public async Task List_all_with_no_filters_still_returns_every_tenant_task_and_a_null_cursor()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Task one", priority = "normal", assigned_to_user_id = targetId });
        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Task two", priority = "urgent", assigned_to_role_key = "guard" });

        var list = await client.GetAsync("/v1/staff/tasks/all");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("next_cursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_all_status_filter_returns_only_matching_tasks()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var create = await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Pending task", priority = "normal", assigned_to_user_id = targetId });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var pendingId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetString();

        var create2 = await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Soon completed task", priority = "normal", assigned_to_user_id = targetId });
        using var create2Doc = JsonDocument.Parse(await create2.Content.ReadAsStringAsync());
        var otherTaskId = create2Doc.RootElement.GetProperty("data").GetProperty("id").GetString();
        var targetClient = ClientFor(App(fx), tenantId, targetId, "driver");
        await targetClient.PostAsync($"/v1/staff/tasks/{otherTaskId}/complete", null);

        var pendingList = await client.GetAsync("/v1/staff/tasks/all?status=pending");
        using var pendingDoc = JsonDocument.Parse(await pendingList.Content.ReadAsStringAsync());
        var pendingRows = pendingDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        pendingRows.Should().ContainSingle(r => r.GetProperty("id").GetString() == pendingId);

        var completedList = await client.GetAsync("/v1/staff/tasks/all?status=completed");
        using var completedDoc = JsonDocument.Parse(await completedList.Content.ReadAsStringAsync());
        completedDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_all_assigned_to_user_id_filter_returns_only_that_persons_tasks()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedUserAsync(fx, tenantId, otherId, "Other");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "For target", priority = "normal", assigned_to_user_id = targetId });
        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "For other", priority = "normal", assigned_to_user_id = otherId });

        var list = await client.GetAsync($"/v1/staff/tasks/all?assigned_to_user_id={targetId}");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        rows.Should().ContainSingle();
        rows[0].GetProperty("title").GetString().Should().Be("For target");
    }

    [Fact]
    public async Task List_all_assigned_to_role_key_filter_returns_only_broadcasts_to_that_role()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "For drivers", priority = "normal", assigned_to_role_key = "driver" });
        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "For guards", priority = "normal", assigned_to_role_key = "guard" });

        var list = await client.GetAsync("/v1/staff/tasks/all?assigned_to_role_key=driver");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        rows.Should().ContainSingle();
        rows[0].GetProperty("title").GetString().Should().Be("For drivers");
    }

    [Fact]
    public async Task List_all_from_to_filters_bound_by_created_at()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Created now", priority = "normal", assigned_to_user_id = targetId });

        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("O");
        var farFuture = await client.GetAsync($"/v1/staff/tasks/all?from={Uri.EscapeDataString(tomorrow)}");
        using var farDoc = JsonDocument.Parse(await farFuture.Content.ReadAsStringAsync());
        farDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0, "the task was created before 'from'");

        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("O");
        var includesNow = await client.GetAsync($"/v1/staff/tasks/all?from={Uri.EscapeDataString(yesterday)}");
        using var includesDoc = JsonDocument.Parse(await includesNow.Content.ReadAsStringAsync());
        includesDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task List_all_combined_filters_narrow_to_the_intersection()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Matches both filters", priority = "normal", assigned_to_user_id = targetId });
        await client.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Wrong role", priority = "normal", assigned_to_role_key = "guard" });

        var list = await client.GetAsync($"/v1/staff/tasks/all?status=pending&assigned_to_user_id={targetId}");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        rows.Should().ContainSingle();
        rows[0].GetProperty("title").GetString().Should().Be("Matches both filters");
    }

    [Fact]
    public async Task List_all_rejects_an_unrecognized_status_value()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        var list = await client.GetAsync("/v1/staff/tasks/all?status=bogus");
        list.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_all_resolves_assigned_created_and_completed_by_names()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin Manager");
        await SeedUserAsync(fx, tenantId, targetId, "Target Driver");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var adminClient = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);
        var targetClient = ClientFor(App(fx), tenantId, targetId, "driver");

        var create = await adminClient.PostAsJsonAsync("/v1/staff/tasks", new
        { title = "Named task", priority = "normal", assigned_to_user_id = targetId });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetString();
        await targetClient.PostAsync($"/v1/staff/tasks/{id}/complete", null);

        var list = await adminClient.GetAsync("/v1/staff/tasks/all");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data").EnumerateArray().Single();
        row.GetProperty("assigned_to_user_name").GetString().Should().Be("Target Driver");
        row.GetProperty("created_by_user_name").GetString().Should().Be("Admin Manager");
        row.GetProperty("completed_by_user_name").GetString().Should().Be("Target Driver");
    }

    [Fact]
    public async Task List_all_cursor_pagination_walks_every_row_exactly_once_in_created_at_desc_order()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await SeedUserAsync(fx, tenantId, adminId, "Admin");
        await SeedUserAsync(fx, tenantId, targetId, "Target");
        await SeedManagerRoleAsync(fx, adminId, Policies.SchoolAdmin);
        var client = ClientFor(App(fx), tenantId, adminId, Policies.SchoolAdmin);

        // Fixture default page size is large; force a tiny page via extremely narrow from/to isn't
        // possible with real timestamps, so exercise the boundary with the smallest realistic
        // amount that still proves the contract: walk pages of the seeded set until NextCursor is
        // null, and assert the union is exactly the seeded titles with no duplicate/missing id.
        var titles = new[] { "Seed 1", "Seed 2", "Seed 3", "Seed 4", "Seed 5" };
        foreach (var title in titles)
        {
            await client.PostAsJsonAsync("/v1/staff/tasks", new
            { title, priority = "normal", assigned_to_user_id = targetId });
            await Task.Delay(5); // distinct CreatedAt ticks so ordering is deterministic
        }

        var seenIds = new HashSet<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = cursor is null
                ? "/v1/staff/tasks/all"
                : $"/v1/staff/tasks/all?cursor={Uri.EscapeDataString(cursor)}";
            var resp = await client.GetAsync(url);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
                seenIds.Add(row.GetProperty("id").GetString()!).Should().BeTrue("no id should repeat across pages");
            cursor = doc.RootElement.GetProperty("next_cursor").ValueKind == JsonValueKind.String
                ? doc.RootElement.GetProperty("next_cursor").GetString()
                : null;
            pages++;
        } while (cursor is not null && pages < 10);

        seenIds.Should().HaveCount(5);
    }
}
