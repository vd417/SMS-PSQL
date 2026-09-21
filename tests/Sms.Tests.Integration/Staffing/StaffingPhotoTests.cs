using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class StaffingPhotoTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
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

    private static async Task<Guid> SeedLinkedTeacherAsync(PostgresFixture fx, Guid tenantId)
    {
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var userId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"IsPlatform\") VALUES (@userId, @tenantId, @email, false)",
            new { userId, tenantId, email = $"teacher{Guid.NewGuid():N}@x.com" });
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@teacherId, @tenantId, 'Linked Teacher', @userId)",
            new { teacherId, tenantId, userId });
        return teacherId;
    }

    private static async Task<string?> GetUserPhotoAsync(PostgresFixture fx, Guid tenantId, Guid teacherId)
    {
        var ctx = new TenantContext(); ctx.Set(tenantId, Guid.NewGuid(), false);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        return await c.QuerySingleAsync<string?>(
            "SELECT u.\"PhotoUrl\" FROM \"dbo\".\"Users\" u JOIN \"dbo\".\"Teachers\" t ON t.\"UserId\" = u.\"Id\" WHERE t.\"Id\" = @teacherId",
            new { teacherId });
    }

    [Fact]
    public async Task Setting_a_linked_teachers_photo_writes_through_to_their_Users_row()
    {
        var tenantId = Guid.NewGuid();
        var teacherId = await SeedLinkedTeacherAsync(fx, tenantId);

        await using var app = App();
        var client = TenantClient(app, tenantId);

        var updated = await Data(await client.PatchAsJsonAsync($"/v1/teachers/{teacherId}",
            new { photo_url = "https://cdn.example.com/teachers/a.png", set_photo = true }), HttpStatusCode.OK);
        updated.GetProperty("name").GetString().Should().Be("Linked Teacher");

        (await GetUserPhotoAsync(fx, tenantId, teacherId)).Should().Be("https://cdn.example.com/teachers/a.png");
    }

    [Fact]
    public async Task Setting_a_photo_on_an_unlinked_teacher_returns_409()
    {
        var tenantId = Guid.NewGuid();
        await using var app = App();
        var client = TenantClient(app, tenantId);

        var created = await Data(await client.PostAsJsonAsync("/v1/teachers", new { name = "Unlinked Teacher" }),
            HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        var res = await client.PatchAsJsonAsync($"/v1/teachers/{id}",
            new { photo_url = "https://cdn.example.com/a.png", set_photo = true });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Rejects_a_teacher_photo_value_that_is_not_a_data_uri_or_http_url()
    {
        var tenantId = Guid.NewGuid();
        var teacherId = await SeedLinkedTeacherAsync(fx, tenantId);

        await using var app = App();
        var client = TenantClient(app, tenantId);

        var res = await client.PatchAsJsonAsync($"/v1/teachers/{teacherId}",
            new { photo_url = "not-a-valid-value", set_photo = true });
        res.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Updating_other_teacher_fields_without_set_photo_leaves_the_photo_untouched()
    {
        var tenantId = Guid.NewGuid();
        var teacherId = await SeedLinkedTeacherAsync(fx, tenantId);

        await using var app = App();
        var client = TenantClient(app, tenantId);

        await client.PatchAsJsonAsync($"/v1/teachers/{teacherId}",
            new { photo_url = "https://cdn.example.com/a.png", set_photo = true });

        var res = await client.PatchAsJsonAsync($"/v1/teachers/{teacherId}", new { status = "inactive" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetUserPhotoAsync(fx, tenantId, teacherId)).Should().Be("https://cdn.example.com/a.png");
    }

    [Fact]
    public async Task Get_teacher_returns_photo_url_from_linked_user()
    {
        var tenantId = Guid.NewGuid();
        var teacherId = await SeedLinkedTeacherAsync(fx, tenantId);
        const string photoUrl = "https://cdn.example.com/teachers/list.png";

        var ctx = new TenantContext(); ctx.Set(tenantId, Guid.NewGuid(), false);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            "UPDATE \"dbo\".\"Users\" u SET \"PhotoUrl\" = @photoUrl FROM \"dbo\".\"Teachers\" t WHERE t.\"UserId\" = u.\"Id\" AND t.\"Id\" = @teacherId",
            new { photoUrl, teacherId });

        await using var app = App();
        var client = TenantClient(app, tenantId);

        var listed = await Data(await client.GetAsync("/v1/teachers"), HttpStatusCode.OK);
        listed.EnumerateArray().First().GetProperty("photo_url").GetString().Should().Be(photoUrl);

        var one = await Data(await client.GetAsync($"/v1/teachers/{teacherId}"), HttpStatusCode.OK);
        one.GetProperty("photo_url").GetString().Should().Be(photoUrl);
    }

    [Fact]
    public async Task Setting_a_linked_teachers_photo_propagates_to_peer_users_with_same_email()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var email = $"multi{Guid.NewGuid():N}@x.com";
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantA, tier: "platinum");
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantB, tier: "platinum");
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"IsPlatform\") VALUES (@userA, @tenantA, @email, false), (@userB, @tenantB, @email, false)",
            new { userA, userB, tenantA, tenantB, email });
        await c.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantA::text, false)", new { tenantA });
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\", \"Email\") VALUES (@teacherId, @tenantA, 'Multi School', @userA, @email)",
            new { teacherId, tenantA, userA, email });

        await using var app = App();
        var client = TenantClient(app, tenantA);
        await Data(await client.PatchAsJsonAsync($"/v1/teachers/{teacherId}",
            new { photo_url = "https://cdn.example.com/shared.png", set_photo = true }), HttpStatusCode.OK);

        var photoB = await c.QuerySingleAsync<string?>(
            "SELECT \"PhotoUrl\" FROM \"dbo\".\"Users\" WHERE \"Id\" = @userB", new { userB });
        photoB.Should().Be("https://cdn.example.com/shared.png");
    }
}
