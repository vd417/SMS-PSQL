using System.IdentityModel.Tokens.Jwt;
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

// NEW-1: two tenant-scoped Users rows sharing the same email and the SAME CreatedAt timestamp
// (as the dev seed inserts multi-school rows in one transaction) must resolve to the SAME
// tenant on every login, not flip based on physical row order (which LastSeenTouchMiddleware
// perturbs by updating LastSeenAt on every authenticated request).
[Collection("sql")]
public class LoginDeterministicTenantTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> AppWithDb() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
            // 10 rapid logins from the same client would otherwise trip the "auth" rate
            // limiter (5/min) before exercising the tie-break logic under test.
            b.UseSetting("RateLimiting:AuthPermitPerMinute", "100");
        });

    [Fact]
    public async Task NEW1_RepeatedLoginAlwaysPicksSameTenantForTiedCreatedAt()
    {
        var hasher = new PasswordHasher();
        var email = $"multi{Guid.NewGuid():N}@x.com";
        var hash = hasher.Hash("Pass123!");
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var sameCreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using (var c = await factory.OpenAsync())
        {
            await c.ExecuteAsync(
                """
                INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier")
                VALUES (@id, 'Tenant A', @slugA, 'active', 'platinum'), (@id2, 'Tenant B', @slugB, 'active', 'platinum')
                """,
                new { id = tenantA, slugA = $"a{tenantA:N}", id2 = tenantB, slugB = $"b{tenantB:N}" });

            // Both rows inserted with the identical CreatedAt, same as a seed transaction default.
            await c.ExecuteAsync(
                """
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "PasswordHash", "IsPlatform", "Status", "CreatedAt")
                VALUES (@userA, @tenantA, @email, @hash, false, 'active', @createdAt),
                       (@userB, @tenantB, @email, @hash, false, 'active', @createdAt)
                """,
                new { userA, tenantA, userB, tenantB, email, hash, createdAt = sameCreatedAt });
        }

        await using var app = AppWithDb();

        var seenTenants = new HashSet<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var client = app.CreateClient();
            var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = System.Text.Json.JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var accessToken = doc.RootElement.GetProperty("data").GetProperty("access_token").GetString();
            accessToken.Should().NotBeNullOrEmpty();

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            var tenantClaim = jwt.Claims.First(c => c.Type == "tenant_id").Value;
            seenTenants.Add(Guid.Parse(tenantClaim));

            // Any authenticated request touches LastSeenAt, which used to perturb physical
            // row order between logins.
            client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
            await client.GetAsync("/v1/auth/me");
        }

        // RED (pre-fix): ORDER BY CASE WHEN "IsPlatform" ... , "CreatedAt" has no tie-breaker,
        // so ties fall back to physical row order, which LastSeenAt updates can flip between
        // logins -> more than one distinct tenant observed across the 10 attempts.
        seenTenants.Should().HaveCount(1, "login must deterministically pick the same tenant for tied CreatedAt rows");
    }
}
