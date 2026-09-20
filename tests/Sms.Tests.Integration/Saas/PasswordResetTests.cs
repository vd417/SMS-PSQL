using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class PasswordResetTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private async Task<string> InsertUserAsync(NpgsqlConnectionFactory factory, string email)
    {
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Users" ("Id", "Email", "IsPlatform") VALUES (gen_random_uuid(),@e,false)""",
            new { e = email });
        return email;
    }

    private static NpgsqlConnectionFactory Factory(PostgresFixture fx)
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private static string Sha256Hex(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    [Fact]
    public async Task Forgot_returns_404_for_unregistered_and_200_for_registered()
    {
        var factory = Factory(fx);
        var email = await InsertUserAsync(factory, $"reset{Guid.NewGuid():N}@x.com");

        await using var app = App();
        var client = app.CreateClient();

        var unknown = await client.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = "nobody-reset@x.com" });
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var err = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()))
            err.RootElement.GetProperty("error").GetProperty("message").GetString()
                .Should().Be("Email is not registered.");

        (await client.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using var c = await factory.OpenAsync();
        var count = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.OtpCodes WHERE Identifier = @id", new { id = email });
        count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reset_with_valid_code_sets_password_and_does_not_return_tokens()
    {
        var factory = Factory(fx);
        var email = await InsertUserAsync(factory, $"reset{Guid.NewGuid():N}@x.com");

        await using var app = App();
        var client = app.CreateClient();

        (await client.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Overwrite the random stored hash with a known code so the test is deterministic.
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE dbo.OtpCodes SET CodeHash = @h WHERE Identifier = @id",
                new { id = email, h = Sha256Hex("123456") });

        var reset = await client.PostAsJsonAsync("/v1/auth/password/reset",
            new { identifier = email, code = "123456", password = "newSecret1" });
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await reset.Content.ReadAsStringAsync()).Should().NotContain("access_token");

        // The new password works at /login.
        var login = await client.PostAsJsonAsync("/v1/auth/login",
            new { email, password = "newSecret1" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reset_with_wrong_code_returns_401()
    {
        var factory = Factory(fx);
        var email = await InsertUserAsync(factory, $"reset{Guid.NewGuid():N}@x.com");

        await using var app = App();
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email });
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE dbo.OtpCodes SET CodeHash = @h WHERE Identifier = @id",
                new { id = email, h = Sha256Hex("123456") });

        (await client.PostAsJsonAsync("/v1/auth/password/reset",
            new { identifier = email, code = "000000", password = "newSecret1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_with_short_password_returns_422()
    {
        var factory = Factory(fx);
        var email = await InsertUserAsync(factory, $"reset{Guid.NewGuid():N}@x.com");

        await using var app = App();
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email });
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE dbo.OtpCodes SET CodeHash = @h WHERE Identifier = @id",
                new { id = email, h = Sha256Hex("123456") });

        (await client.PostAsJsonAsync("/v1/auth/password/reset",
            new { identifier = email, code = "123456", password = "short" }))
            .StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Reset_with_missing_code_returns_401()
    {
        var factory = Factory(fx);
        var email = await InsertUserAsync(factory, $"reset{Guid.NewGuid():N}@x.com");

        await using var app = App();
        var client = app.CreateClient();

        await client.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email });
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE dbo.OtpCodes SET CodeHash = @h WHERE Identifier = @id",
                new { id = email, h = Sha256Hex("123456") });

        // POST with no code field — should return 401, not 500
        (await client.PostAsJsonAsync("/v1/auth/password/reset",
            new { identifier = email, password = "newSecret1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
