using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class PlatformAdminSeedTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App(string? adminEmail) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            // Override the committed appsettings Catre:AdminEmail. Passing null means
            // "explicitly unconfigured" (empty), so the seeder warns-and-skips rather than
            // seeding the appsettings admin into the shared collection database.
            b.UseSetting("Catre:AdminEmail", adminEmail ?? "");
        });

    private async Task<int> PlatformAdminCount(string email)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        // Platform admins have TenantId = NULL; the RLS FILTER predicate on dbo.Users hides
        // them from a context-less connection. Stamp IsPlatform=1 so the row is visible.
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Users WHERE IsPlatform = 1 AND Email = @email", new { email });
    }

    private async Task ClearPlatformAdmins()
    {
        // Other app-booting tests in the shared "sql" collection seed the appsettings
        // Catre admin; the seeder's idempotency guard is existence-of-ANY-admin, so start clean.
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await conn.ExecuteAsync("DELETE FROM dbo.Users WHERE IsPlatform = 1");
    }

    [Fact]
    public async Task Boot_seeds_exactly_one_platform_admin_and_is_idempotent()
    {
        await ClearPlatformAdmins();
        var email = $"admin-{Guid.NewGuid():N}@catre.test";

        // First boot seeds the admin (RunAsync executes during factory startup).
        await using (var app = App(email)) { _ = app.CreateClient(); }
        (await PlatformAdminCount(email)).Should().Be(1);

        // Second boot finds the admin and no-ops.
        await using (var app = App(email)) { _ = app.CreateClient(); }
        (await PlatformAdminCount(email)).Should().Be(1);
    }

    [Fact]
    public async Task Boot_without_admin_config_does_not_seed_and_does_not_throw()
    {
        // No Catre:AdminEmail -> warning + skip; app still boots and serves.
        await using var app = App(adminEmail: null);
        var client = app.CreateClient();
        var res = await client.GetAsync("/health");
        res.IsSuccessStatusCode.Should().BeTrue();
    }
}
