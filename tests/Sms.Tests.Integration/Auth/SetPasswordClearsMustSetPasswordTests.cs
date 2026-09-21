using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class SetPasswordClearsMustSetPasswordTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> AppWithDb() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    [Fact]
    public async Task SetPassword_success_clears_MustSetPassword()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name, MustSetPassword) " +
                "VALUES (@userId, @tenantId, @email, @hash, 'New Teacher', 1)",
                new { userId, tenantId, email = $"n{Guid.NewGuid():N}@x.com", hash = hasher.Hash("Temp1234!") });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.teacher" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var meBefore = await client.GetAsync("/v1/auth/me");
        using (var beforeDoc = JsonDocument.Parse(await meBefore.Content.ReadAsStringAsync()))
            beforeDoc.RootElement.GetProperty("data").GetProperty("must_set_password").GetBoolean().Should().BeTrue();

        var setRes = await client.PostAsJsonAsync("/v1/auth/set-password", new { Password = "Permanent1234!" });
        setRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var meAfter = await client.GetAsync("/v1/auth/me");
        using var afterDoc = JsonDocument.Parse(await meAfter.Content.ReadAsStringAsync());
        afterDoc.RootElement.GetProperty("data").GetProperty("must_set_password").GetBoolean().Should().BeFalse();
    }
}
