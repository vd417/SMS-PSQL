using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class AuthFlowTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> AppWithDb() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    [Fact]
    public async Task Login_with_seeded_user_returns_tokens_and_me_works()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"admin{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await login.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var accessToken = doc.RootElement.GetProperty("data").GetProperty("access_token").GetString();
        accessToken.Should().NotBeNullOrEmpty();

        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        var me = await client.GetAsync("/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_with_mobile_number_returns_tokens()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var phone = $"+9198{Random.Shared.Next(10_000_000, 99_999_999)}";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Phone, PasswordHash, IsPlatform) VALUES (NEWID(),@p,@h,1)",
                new { p = phone, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();

        // No '@' → backend looks the user up by phone instead of email.
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { phone, password = "Pass123!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await login.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("data").GetProperty("access_token").GetString()
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Me_exposes_is_platform_for_a_platform_user()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"plat{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var me = await client.GetAsync("/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = System.Text.Json.JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("is_platform").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Me_returns_photo_url_when_set_directly_on_the_row()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"photo{Guid.NewGuid():N}@x.com";
        const string photoUrl = "https://cdn.example.com/avatars/a.png";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform, PhotoUrl) VALUES (NEWID(),@e,@h,1,@p)",
                new { e = email, h = hasher.Hash("Pass123!"), p = photoUrl });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var me = await client.GetAsync("/v1/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = System.Text.Json.JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("photo_url").GetString().Should().Be(photoUrl);
    }

    [Fact]
    public async Task Me_returns_null_photo_url_when_unset()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"nophoto{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var me = await client.GetAsync("/v1/auth/me");
        using var doc = System.Text.Json.JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("photo_url").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdatePhoto_sets_and_then_clears_the_signed_in_users_photo()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"setphoto{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var set = await client.PatchAsJsonAsync("/v1/me/photo", new { photo_url = "https://cdn.example.com/a.png" });
        set.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me1 = await client.GetAsync("/v1/auth/me");
        using (var doc1 = System.Text.Json.JsonDocument.Parse(await me1.Content.ReadAsStringAsync()))
            doc1.RootElement.GetProperty("data").GetProperty("photo_url").GetString()
                .Should().Be("https://cdn.example.com/a.png");

        var clear = await client.PatchAsJsonAsync("/v1/me/photo", new { photo_url = (string?)null });
        clear.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me2 = await client.GetAsync("/v1/auth/me");
        using var doc2 = System.Text.Json.JsonDocument.Parse(await me2.Content.ReadAsStringAsync());
        doc2.RootElement.GetProperty("data").GetProperty("photo_url").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdatePhoto_rejects_a_value_that_is_not_a_data_uri_or_http_url()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"badphoto{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.PatchAsJsonAsync("/v1/me/photo", new { photo_url = "not-a-valid-value" });
        res.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task UpdatePhoto_rejects_a_value_over_400000_characters()
    {
        var hasher = new PasswordHasher();
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"bigphoto{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.Users (Id, Email, PasswordHash, IsPlatform) VALUES (NEWID(),@e,@h,1)",
                new { e = email, h = hasher.Hash("Pass123!") });

        await using var app = AppWithDb();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        var token = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var oversized = "data:image/png;base64," + new string('a', 400_001);
        var res = await client.PatchAsJsonAsync("/v1/me/photo", new { photo_url = oversized });
        res.StatusCode.Should().Be((HttpStatusCode)422);
    }
}
