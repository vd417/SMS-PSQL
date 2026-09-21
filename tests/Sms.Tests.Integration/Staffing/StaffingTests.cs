using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Staffing;

[Collection("sql")]
public class StaffingTests(PostgresFixture fx)
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

    [Fact]
    public async Task Teacher_create_get_list_update_with_subjects()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var created = await Data(await client.PostAsJsonAsync("/v1/teachers", new
        {
            name = "R. Kumar", gender = "M", department = "Mathematics", designation = "Senior Teacher",
            subjects = new[] { "Maths", "Physics" }, phone = "+91 90000 11111", email = "kumar@school.edu",
            exp = 12, rating = 4.5, result = 92, load = 24, avatar_hue = 210, top = true
        }), HttpStatusCode.Created);

        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("subjects").EnumerateArray().Select(e => e.GetString()).Should().Contain("Physics");
        created.GetProperty("top").GetBoolean().Should().BeTrue();
        created.GetProperty("status").GetString().Should().Be("active");

        (await Data(await client.GetAsync($"/v1/teachers/{id}"), HttpStatusCode.OK))
            .GetProperty("department").GetString().Should().Be("Mathematics");

        var list = await Data(await client.GetAsync("/v1/teachers?dept=Mathematics"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        var updated = await Data(await client.PatchAsJsonAsync($"/v1/teachers/{id}",
            new { status = "inactive" }), HttpStatusCode.OK);
        updated.GetProperty("status").GetString().Should().Be("inactive");
    }

    [Fact]
    public async Task Teacher_get_and_list_report_the_linked_UserId_when_present()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = TenantClient(app, tenantId);

        var created = await Data(await client.PostAsJsonAsync("/v1/teachers", new
        {
            name = "L. Menon", gender = "F", department = "Science", designation = "Teacher",
            subjects = Array.Empty<string>(), phone = "+91 90000 22222", email = "menon@school.edu",
            exp = 5, rating = 4.0, result = 88, load = 20, avatar_hue = 100, top = false
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("user_id").ValueKind.Should().Be(JsonValueKind.Null);

        var linkedUserId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name, Email) VALUES (@Id, @TenantId, 'Linked User', @Email)",
                new { Id = linkedUserId, TenantId = tenantId, Email = $"linked-{linkedUserId}@test.local" });
            await conn.ExecuteAsync("UPDATE dbo.Teachers SET UserId = @UserId WHERE Id = @Id",
                new { UserId = linkedUserId, Id = id });
        }

        (await Data(await client.GetAsync($"/v1/teachers/{id}"), HttpStatusCode.OK))
            .GetProperty("user_id").GetGuid().Should().Be(linkedUserId);

        var list = await Data(await client.GetAsync("/v1/teachers"), HttpStatusCode.OK);
        list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == id)
            .GetProperty("user_id").GetGuid().Should().Be(linkedUserId);
    }

    [Fact]
    public async Task Staff_create_and_list_filter_by_category()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TenantClient(app, tenantId);

        var created = await Data(await client.PostAsJsonAsync("/v1/staff", new
        {
            name = "Ramesh", gender = "M", role = "Bus Driver", category = "transport",
            department = "Transport", phone = "+91 98888 77777", shift = "Morning", route = "Route 7"
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("category").GetString().Should().Be("transport");

        var list = await Data(await client.GetAsync("/v1/staff?cat=transport"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);
    }

    [Fact]
    public async Task Teachers_are_isolated_across_tenants_by_rls()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientA = TenantClient(app, tenantA);
        var clientB = TenantClient(app, tenantB);

        var created = await Data(await clientA.PostAsJsonAsync("/v1/teachers", new
        {
            name = "Tenant A Teacher", department = "Science"
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        (await clientB.GetAsync($"/v1/teachers/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var bList = await Data(await clientB.GetAsync("/v1/teachers"), HttpStatusCode.OK);
        bList.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().NotContain(id);
    }
}
