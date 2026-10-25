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

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ClassDeleteTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.SchoolAdmin }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<Guid> ClassId(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Delete_removes_a_class_with_no_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = AdminClient(app, tenantId);

        var id = await ClassId(await client.PostAsJsonAsync("/v1/classes",
            new { name = "VII-D", grade = "VII", section = "D" }));

        (await client.DeleteAsync($"/v1/classes/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var listDoc = JsonDocument.Parse(
            await (await client.GetAsync("/v1/classes")).Content.ReadAsStringAsync());
        listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid())
            .Should().NotContain(id);
    }

    [Fact]
    public async Task Delete_is_blocked_when_the_class_has_active_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"Id\", \"TenantId\", \"Name\", \"Grade\", \"Section\", \"StudentCount\") VALUES (@classId, @tenantId, 'VIII-A', '8', 'A', 0)",
                new { classId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"TenantId\", \"AdmissionNo\", \"Name\", \"Grade\", \"Section\", \"Status\") VALUES (@tenantId, 'ADM1', 'Pupil One', '8', 'A', 'active')",
                new { tenantId });
        }

        var client = AdminClient(app, tenantId);
        var res = await client.DeleteAsync($"/v1/classes/{classId}");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Still listed — not deleted.
        using var listDoc = JsonDocument.Parse(
            await (await client.GetAsync("/v1/classes")).Content.ReadAsStringAsync());
        listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid())
            .Should().Contain(classId);
    }

    [Fact]
    public async Task Delete_returns_404_for_an_unknown_class()
    {
        await using var app = App();
        var client = AdminClient(app, Guid.NewGuid());
        (await client.DeleteAsync($"/v1/classes/{Guid.NewGuid()}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }
}
