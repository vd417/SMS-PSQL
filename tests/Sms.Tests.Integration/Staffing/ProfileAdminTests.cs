using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

/// Admin/principal CRUD for a staff member's documents (staff/{id}/documents), and its effect
/// on the staff self-service read (GET /v1/staff/profile) added in the previous task.
[Collection("sql")]
public class ProfileAdminTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient ClientWithRole(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId) =>
        ClientWithRole(app, tenantId, userId, "school.admin");

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> Error(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").Clone();
    }

    private async Task<Guid> InsertTierAllowedStaffAsync(Guid tenantId, Guid? userId = null)
    {
        var staffId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@Id, @TenantId, @Name, @UserId)",
            new { Id = staffId, TenantId = tenantId, Name = "Ramesh Kumar", UserId = userId });
        return staffId;
    }

    [Fact]
    public async Task Admin_creates_a_document()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        var data = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Driving licence", value = "DL-0420190012345", ok = true }), HttpStatusCode.Created);

        data.GetProperty("label").GetString().Should().Be("Driving licence");
        data.GetProperty("value").GetString().Should().Be("DL-0420190012345");
        data.GetProperty("ok").GetBoolean().Should().BeTrue();
        data.GetProperty("id").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Admin_updates_a_document()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());
        var created = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Driving licence", value = "DL-OLD", ok = false }), HttpStatusCode.Created);
        var docId = created.GetProperty("id").GetGuid();

        var updated = await Data(await admin.PatchAsJsonAsync($"/v1/staff/{staffId}/documents/{docId}",
            new { label = "Driving licence", value = "DL-NEW", ok = true }), HttpStatusCode.OK);

        updated.GetProperty("value").GetString().Should().Be("DL-NEW");
        updated.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Admin_deletes_a_document()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());
        var created = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Bus fitness", value = "Valid till 2027-03", ok = true }), HttpStatusCode.Created);
        var docId = created.GetProperty("id").GetGuid();

        (await admin.DeleteAsync($"/v1/staff/{staffId}/documents/{docId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(await admin.GetAsync($"/v1/staff/{staffId}/documents"), HttpStatusCode.OK);
        list.EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Staff_profile_returns_a_newly_created_document()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId, staffUserId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "ID verified", value = "Yes", ok = true });

        var staffClient = ClientWithRole(app, tenantId, staffUserId, "driver");
        var profile = await Data(await staffClient.GetAsync("/v1/staff/profile"), HttpStatusCode.OK);
        var docs = profile.GetProperty("documents").EnumerateArray().ToList();

        docs.Should().ContainSingle(d => d.GetProperty("label").GetString() == "ID verified");
    }

    [Fact]
    public async Task Staff_profile_reflects_an_update()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId, staffUserId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());
        var created = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Bus fitness", value = "Valid till 2026-01", ok = false }), HttpStatusCode.Created);
        var docId = created.GetProperty("id").GetGuid();

        await admin.PatchAsJsonAsync($"/v1/staff/{staffId}/documents/{docId}",
            new { label = "Bus fitness", value = "Valid till 2027-03", ok = true });

        var staffClient = ClientWithRole(app, tenantId, staffUserId, "driver");
        var profile = await Data(await staffClient.GetAsync("/v1/staff/profile"), HttpStatusCode.OK);
        var doc = profile.GetProperty("documents").EnumerateArray().Single();

        doc.GetProperty("value").GetString().Should().Be("Valid till 2027-03");
        doc.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Staff_profile_no_longer_returns_a_deleted_document()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId, staffUserId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());
        var created = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "ID verified", value = "Yes", ok = true }), HttpStatusCode.Created);
        var docId = created.GetProperty("id").GetGuid();

        await admin.DeleteAsync($"/v1/staff/{staffId}/documents/{docId}");

        var staffClient = ClientWithRole(app, tenantId, staffUserId, "driver");
        var profile = await Data(await staffClient.GetAsync("/v1/staff/profile"), HttpStatusCode.OK);

        profile.GetProperty("documents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Tenant_isolation_admin_cannot_manage_a_staff_member_in_another_tenant()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var staffIdInA = await InsertTierAllowedStaffAsync(tenantA);
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantB, tier: "platinum");
        var adminOfB = AdminClient(app, tenantB, Guid.NewGuid());

        var err = await Error(await adminOfB.PostAsJsonAsync($"/v1/staff/{staffIdInA}/documents",
            new { label = "Driving licence", value = "DL-X", ok = true }), HttpStatusCode.NotFound);
        err.GetProperty("code").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task Unauthorized_role_cannot_manage_documents()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var driver = ClientWithRole(app, tenantId, Guid.NewGuid(), "driver");

        (await driver.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Driving licence", value = "DL-X", ok = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invalid_staff_id_returns_not_found()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        var err = await Error(await admin.PostAsJsonAsync($"/v1/staff/{Guid.NewGuid()}/documents",
            new { label = "Driving licence", value = "DL-X", ok = true }), HttpStatusCode.NotFound);
        err.GetProperty("code").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task Invalid_document_id_returns_not_found_on_update_and_delete()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        (await admin.PatchAsJsonAsync($"/v1/staff/{staffId}/documents/{Guid.NewGuid()}",
            new { label = "X", value = "Y", ok = (bool?)null })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.DeleteAsync($"/v1/staff/{staffId}/documents/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_label_or_value_is_rejected()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        (await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "", value = "DL-X", ok = true })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "Driving licence", value = "", ok = true })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Admin_list_is_empty_for_a_staff_member_with_no_documents()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        var data = await Data(await admin.GetAsync($"/v1/staff/{staffId}/documents"), HttpStatusCode.OK);

        data.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Null_ok_round_trips_through_create_and_update()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        var created = await Data(await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents",
            new { label = "ID verified", value = "Pending", ok = (bool?)null }), HttpStatusCode.Created);
        created.GetProperty("ok").ValueKind.Should().Be(JsonValueKind.Null);

        var docId = created.GetProperty("id").GetGuid();
        var updated = await Data(await admin.PatchAsJsonAsync($"/v1/staff/{staffId}/documents/{docId}",
            new { label = "ID verified", value = "Pending", ok = (bool?)null }), HttpStatusCode.OK);
        updated.GetProperty("ok").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Admin_list_is_ordered_by_created_at()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var staffId = await InsertTierAllowedStaffAsync(tenantId);
        var admin = AdminClient(app, tenantId, Guid.NewGuid());

        await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents", new { label = "First", value = "1", ok = (bool?)null });
        await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents", new { label = "Second", value = "2", ok = (bool?)null });
        await admin.PostAsJsonAsync($"/v1/staff/{staffId}/documents", new { label = "Third", value = "3", ok = (bool?)null });

        var data = await Data(await admin.GetAsync($"/v1/staff/{staffId}/documents"), HttpStatusCode.OK);
        var labels = data.EnumerateArray().Select(d => d.GetProperty("label").GetString()).ToList();

        labels.Should().ContainInOrder("First", "Second", "Third");
    }
}
