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

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class AnnouncementCreatorNameTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Announcement_From_resolves_to_creator_name_not_role()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(principalUserId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var create = await client.PostAsJsonAsync("/v1/announcements", new
        {
            title = "Test Notice", body = "Body text", type = "general", audience = "everyone", channels = new string[] { }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetAsync("/v1/announcements");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        var found = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("title").GetString() == "Test Notice")
            {
                row.GetProperty("from").GetString().Should().Be("Priya Principal");
                found = true;
            }
        }
        found.Should().BeTrue();
    }
}
