using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class AuthRolesTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private static async Task<(string access, string refresh)> LoginAsync(HttpClient client, string email)
    {
        var res = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        return (data.GetProperty("access_token").GetString()!, data.GetProperty("refresh_token").GetString()!);
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string access)
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Login_issues_roles_from_user_roles_table()
    {
        var hasher = new PasswordHasher();
        var factory = PlatformFactory();
        var email = $"plat{Guid.NewGuid():N}@x.com";
        Guid userId;
        await using (var c = await factory.OpenAsync())
        {
            userId = await c.QuerySingleAsync<Guid>(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"Email\", \"PasswordHash\", \"IsPlatform\") VALUES (gen_random_uuid(),@e,@h,true) RETURNING \"Id\"",
                new { e = email, h = hasher.Hash("Pass123!") });
            await c.ExecuteAsync("INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@u,'owner'),(@u,'support')",
                new { u = userId });
        }

        await using var app = App();
        var client = app.CreateClient();
        var (access, _) = await LoginAsync(client, email);
        var me = await MeAsync(client, access);

        var roles = me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToArray();
        roles.Should().Contain("owner").And.Contain("support");
        me.GetProperty("tenant_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Refresh_reloads_tenant_and_roles_for_tenant_user()
    {
        var hasher = new PasswordHasher();
        var factory = PlatformFactory();
        var tenantId = Guid.NewGuid();
        var email = $"ten{Guid.NewGuid():N}@x.com";
        Guid userId;
        await using (var c = await factory.OpenAsync())
        {
            await c.ExecuteAsync("INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\") VALUES (@t,'T',@s)",
                new { t = tenantId, s = "t-" + tenantId.ToString("N") });
            userId = await c.QuerySingleAsync<Guid>(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"PasswordHash\", \"IsPlatform\") VALUES (gen_random_uuid(),@t,@e,@h,false) RETURNING \"Id\"",
                new { t = tenantId, e = email, h = hasher.Hash("Pass123!") });
            await c.ExecuteAsync("INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@u,'school.admin')", new { u = userId });
        }

        await using var app = App();
        var client = app.CreateClient();
        var (_, refresh) = await LoginAsync(client, email);

        var refreshRes = await client.PostAsJsonAsync("/v1/auth/refresh", new { refresh_token = refresh });
        refreshRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rdoc = JsonDocument.Parse(await refreshRes.Content.ReadAsStringAsync());
        var newAccess = rdoc.RootElement.GetProperty("data").GetProperty("access_token").GetString()!;

        var me = await MeAsync(client, newAccess);
        me.GetProperty("tenant_id").GetString().Should().Be(tenantId.ToString());
        me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should().Contain("school.admin");
    }
}
