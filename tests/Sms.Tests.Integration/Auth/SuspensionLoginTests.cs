using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class SuspensionLoginTests(PostgresFixture fx)
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

    private async Task<string> SeedInactiveUserAsync(string role)
    {
        var hasher = new PasswordHasher();
        var tenantId = Guid.NewGuid();
        var email = $"u{Guid.NewGuid():N}@x.com";
        var factory = PlatformFactory();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync("INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\") VALUES (@t,'T',@s,'active')",
            new { t = tenantId, s = "t-" + tenantId.ToString("N") });
        var userId = await c.QuerySingleAsync<Guid>(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"PasswordHash\", \"Status\", \"IsPlatform\") VALUES (gen_random_uuid(),@t,@e,@h,'inactive',false) RETURNING \"Id\"",
            new { t = tenantId, e = email, h = hasher.Hash("Pass123!") });
        await c.ExecuteAsync("INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@u,@r)", new { u = userId, r = role });
        return email;
    }

    private static async Task<(HttpStatusCode Status, string Code, string Message)> LoginAsync(HttpClient client, string email)
    {
        var res = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        return (res.StatusCode, error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Inactive_teacher_sees_the_suspension_message()
    {
        var email = await SeedInactiveUserAsync(Policies.Teacher);
        await using var app = App();
        var (status, code, message) = await LoginAsync(app.CreateClient(), email);

        status.Should().Be(HttpStatusCode.Forbidden);
        code.Should().Be("access_suspended");
        message.Should().Be("Your account has been suspended by your school. Please contact your school administrator.");
    }

    [Fact]
    public async Task Inactive_staff_sees_the_suspension_message()
    {
        var email = await SeedInactiveUserAsync(Policies.Staff);
        await using var app = App();
        var (status, code, _) = await LoginAsync(app.CreateClient(), email);

        status.Should().Be(HttpStatusCode.Forbidden);
        code.Should().Be("access_suspended");
    }

    [Fact]
    public async Task Inactive_admin_keeps_the_original_deactivated_message()
    {
        var email = await SeedInactiveUserAsync(Policies.SchoolAdmin);
        await using var app = App();
        var (status, code, message) = await LoginAsync(app.CreateClient(), email);

        status.Should().Be(HttpStatusCode.Forbidden);
        code.Should().Be("access_inactive");
        message.Should().Be("Your access to this school has been deactivated by the admin.");
    }
}
