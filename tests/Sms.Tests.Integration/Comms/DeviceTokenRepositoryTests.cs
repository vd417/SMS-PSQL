using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class DeviceTokenRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static DeviceTokenRepository RepoFor(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, out IServiceScope scope)
    {
        scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        return scope.ServiceProvider.GetRequiredService<DeviceTokenRepository>();
    }

    [Fact]
    public async Task Upsert_then_list_round_trips_the_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";
        var repo = RepoFor(app, tenantId, userId, out var scope);
        using (scope)
        {
            await repo.UpsertAsync(tenantId, userId, token, "ios");
            (await repo.ListTokensForUserAsync(tenantId, userId)).Should().ContainSingle().Which.Should().Be(token);
        }
    }

    [Fact]
    public async Task Re_registering_a_token_moves_it_to_the_new_user_without_duplicating()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var oldUser = Guid.NewGuid();
        var newUser = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var repo1 = RepoFor(app, tenantId, oldUser, out var s1);
        using (s1) await repo1.UpsertAsync(tenantId, oldUser, token, "ios");

        var repo2 = RepoFor(app, tenantId, newUser, out var s2);
        using (s2)
        {
            await repo2.UpsertAsync(tenantId, newUser, token, "android");
            (await repo2.ListTokensForUserAsync(tenantId, newUser)).Should().ContainSingle();
            (await repo2.ListTokensForUserAsync(tenantId, oldUser)).Should().BeEmpty("the token moved to the new user");
        }
    }

    [Fact]
    public async Task A_token_registered_in_two_tenants_is_one_row_per_tenant()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var repoA = RepoFor(app, tenantA, user, out var sa);
        using (sa) await repoA.UpsertAsync(tenantA, user, token, "ios");
        var repoB = RepoFor(app, tenantB, user, out var sb);
        using (sb)
        {
            await repoB.UpsertAsync(tenantB, user, token, "ios");
            (await repoB.ListTokensForUserAsync(tenantB, user)).Should().ContainSingle();
        }
    }
}
