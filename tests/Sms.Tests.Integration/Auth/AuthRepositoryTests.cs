using Dapper;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class AuthRepositoryTests(PostgresFixture fx)
{
    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    [Fact]
    public async Task GetByEmail_returns_seeded_user()
    {
        var factory = PlatformFactory();
        var email = $"u{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                """INSERT INTO "dbo"."Users" ("Id", "Email", "PasswordHash", "IsPlatform") VALUES (gen_random_uuid(),@e,'h',true)""",
                new { e = email });

        var repo = new AuthRepository(factory);
        var user = await repo.GetByEmailAsync(email);
        user!.Email.Should().Be(email);
    }

    [Fact]
    public async Task Refresh_token_insert_then_get_active_then_revoke()
    {
        var factory = PlatformFactory();
        Guid userId;
        await using (var c = await factory.OpenAsync())
            userId = await c.QuerySingleAsync<Guid>(
                """INSERT INTO "dbo"."Users" ("Id", "Email", "IsPlatform") VALUES (gen_random_uuid(),@e,true) RETURNING "Id" """,
                new { e = $"r{Guid.NewGuid():N}@x.com" });

        var store = new RefreshTokenStore(factory);
        await store.SaveAsync(userId, "hash-1", DateTime.UtcNow.AddDays(7));
        (await store.GetActiveUserIdAsync("hash-1")).Should().Be(userId);
        await store.RevokeAsync("hash-1");
        (await store.GetActiveUserIdAsync("hash-1")).Should().BeNull();
    }
}
