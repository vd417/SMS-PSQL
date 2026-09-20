using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

/// GET /v1/staff/profile — the logged-in staff member's own document list, resolved from their
/// login identity (Staff.UserId), never from a client-supplied id. See task-5 spec: no fake/
/// seeded documents ship with this endpoint, so every case here seeds its own rows (or none).
[Collection("sql")]
public class ProfileTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient StaffClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
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

    private static async Task InsertStaffAsync(string cs, Guid staffId, Guid tenantId, Guid userId, string name)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Staff (Id, TenantId, Name, UserId) VALUES (@Id, @TenantId, @Name, @UserId)",
            new { Id = staffId, TenantId = tenantId, Name = name, UserId = userId });
    }

    private static async Task InsertDocumentAsync(
        string cs, Guid tenantId, Guid staffId, string label, string value, bool? ok, DateTime createdAt)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.StaffDocuments (Id, TenantId, StaffId, Label, Value, Ok, CreatedAt) " +
            "VALUES (NEWID(), @TenantId, @StaffId, @Label, @Value, @Ok, @CreatedAt)",
            new { TenantId = tenantId, StaffId = staffId, Label = label, Value = value, Ok = ok, CreatedAt = createdAt });
    }

    [Fact]
    public async Task Staff_user_with_no_documents_gets_an_empty_list()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, Guid.NewGuid(), tenantId, userId, "Ramesh Kumar");

        var data = await Data(await StaffClient(app, tenantId, userId).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);

        data.GetProperty("documents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Authenticated_user_with_no_staff_row_gets_an_empty_list_not_an_error()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid(); // never inserted into dbo.Staff

        var data = await Data(await StaffClient(app, tenantId, userId).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);

        data.GetProperty("documents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Staff_user_with_multiple_documents_gets_them_ordered_by_created_at_oldest_first()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, staffId, tenantId, userId, "Ramesh Kumar");

        var now = DateTime.UtcNow;
        // Inserted out of chronological order on purpose — the response must still come back
        // ordered by CreatedAt, not insertion order.
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffId, "Bus fitness", "Valid till 2027-03", true, now.AddDays(-1));
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffId, "Driving licence", "DL-0420190012345", true, now.AddDays(-10));
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffId, "ID verified", "Yes", null, now);

        var data = await Data(await StaffClient(app, tenantId, userId).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);
        var documents = data.GetProperty("documents").EnumerateArray().ToList();

        documents.Should().HaveCount(3);
        documents.Select(d => d.GetProperty("label").GetString())
            .Should().ContainInOrder("Driving licence", "Bus fitness", "ID verified");
    }

    [Fact]
    public async Task Ok_field_round_trips_as_null_when_not_set()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, staffId, tenantId, userId, "Ramesh Kumar");
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffId, "ID verified", "Pending", null, DateTime.UtcNow);

        var data = await Data(await StaffClient(app, tenantId, userId).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);
        var doc = data.GetProperty("documents").EnumerateArray().Single();

        doc.GetProperty("ok").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Response_shape_uses_snake_case_keys_and_a_data_envelope()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, staffId, tenantId, userId, "Ramesh Kumar");
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffId, "Driving licence", "DL-0420190012345", true, DateTime.UtcNow);

        var res = await StaffClient(app, tenantId, userId).GetAsync("/v1/staff/profile");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var item = data.GetProperty("documents").EnumerateArray().Single();

        item.TryGetProperty("id", out _).Should().BeTrue();
        item.TryGetProperty("label", out _).Should().BeTrue();
        item.TryGetProperty("value", out _).Should().BeTrue();
        item.TryGetProperty("ok", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_isolation_a_staff_row_with_the_same_user_id_in_another_tenant_is_invisible()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var staffAId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, staffAId, tenantA, userId, "Ramesh Kumar (Tenant A)");
        await InsertDocumentAsync(fx.ConnectionString, tenantA, staffAId, "Driving licence", "DL-A", true, DateTime.UtcNow);

        // Same UserId, but the caller's JWT is scoped to a different tenant that has no matching
        // Staff row — must see nothing from Tenant A, not Tenant A's documents.
        var data = await Data(await StaffClient(app, tenantB, userId).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);

        data.GetProperty("documents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Another_staff_members_documents_in_the_same_tenant_are_never_returned()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var staffAId = Guid.NewGuid();
        var staffBId = Guid.NewGuid();
        await InsertStaffAsync(fx.ConnectionString, staffAId, tenantId, userA, "Staff A");
        await InsertStaffAsync(fx.ConnectionString, staffBId, tenantId, userB, "Staff B");
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffAId, "Driving licence", "DL-A", true, DateTime.UtcNow);
        await InsertDocumentAsync(fx.ConnectionString, tenantId, staffBId, "Driving licence", "DL-B", true, DateTime.UtcNow);

        var dataForA = await Data(await StaffClient(app, tenantId, userA).GetAsync("/v1/staff/profile"), HttpStatusCode.OK);
        var docsForA = dataForA.GetProperty("documents").EnumerateArray().ToList();

        docsForA.Should().ContainSingle();
        docsForA[0].GetProperty("value").GetString().Should().Be("DL-A");
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        await using var app = App();
        var client = app.CreateClient();

        (await client.GetAsync("/v1/staff/profile")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
