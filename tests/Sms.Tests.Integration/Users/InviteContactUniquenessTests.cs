using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Users;

// B5: UserService.InviteAsync is wired to the ContactClaims ledger via IContactValidator.
// The pre-existing ListByTenantAsync pre-check still rejects the common same-tenant duplicate
// before insert; this adds authoritative enforcement (cross-owner collisions / races) and
// populates the ledger for each newly invited user. Exercised end-to-end through POST /v1/users.
[Collection("sql")]
public class InviteContactUniquenessTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private async Task<Guid> SeedActiveTenant()
    {
        var id = Guid.NewGuid();
        await using var c = await PlatformFactory().OpenAsync();
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@id,'T',@s,'active','gold')""",
            new { id, s = $"t{id:N}" });
        return id;
    }

    // Pre-claims a contact for a DIFFERENT owner, so the Users pre-check (which only scans
    // dbo.Users) passes but the ledger sync conflicts — isolating the new enforcement path.
    private async Task SeedForeignEmailClaim(Guid tenantId, string normalizedEmail)
    {
        await using var c = await PlatformFactory().OpenAsync();
        await c.ExecuteAsync(
            """
            INSERT INTO "dbo"."ContactClaims" ("TenantId", "Kind", "NormalizedValue", "OwnerType", "OwnerId", "PersonId")
            VALUES (@t, 'email', @v, 'staff', 'S1', @p)
            """,
            new { t = tenantId, v = normalizedEmail, p = Guid.NewGuid() });
    }

    private async Task<int> UserCountByEmailAsync(Guid tenantId, string email)
    {
        await using var c = await PlatformFactory().OpenAsync();
        var rows = await c.QueryAsync<int>(
            """SELECT COUNT(1) FROM "dbo"."Users" WHERE "TenantId" = @t AND lower("Email") = lower(@e)""",
            new { t = tenantId, e = email });
        return rows.First();
    }

    private sealed record ClaimRow(string Kind, string NormalizedValue, string OwnerType, string OwnerId);

    private async Task<IReadOnlyList<ClaimRow>> ClaimsAsync(Guid tenantId)
    {
        await using var c = await PlatformFactory().OpenAsync();
        var rows = await c.QueryAsync<ClaimRow>(
            """SELECT "Kind", "NormalizedValue", "OwnerType", "OwnerId" FROM "dbo"."ContactClaims" WHERE "TenantId" = @t""",
            new { t = tenantId });
        return rows.AsList();
    }

    [Fact]
    public async Task Invite_with_email_already_claimed_by_another_owner_is_rejected_409()
    {
        var tid = await SeedActiveTenant();
        var email = $"taken{Guid.NewGuid():N}@x.com"; // already lowercase == normalized form
        await SeedForeignEmailClaim(tid, email);
        await using var app = App();
        var admin = AdminClient(app, tid);

        var res = await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // The conflict must not leave an orphaned Users row behind (User_Create commits before the
        // ledger sync; a failed sync has to compensate). An orphan would make the pre-check reject
        // every later invite of this contact permanently.
        (await UserCountByEmailAsync(tid, email)).Should().Be(0);
    }

    [Fact]
    public async Task Invite_rejected_by_the_ledger_can_be_retried_once_the_foreign_claim_is_gone()
    {
        var tid = await SeedActiveTenant();
        var email = $"retry{Guid.NewGuid():N}@x.com";
        await SeedForeignEmailClaim(tid, email);
        await using var app = App();
        var admin = AdminClient(app, tid);

        (await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Clearing the foreign claim must leave the tenant able to invite the contact — i.e. the
        // first, rejected attempt left no ghost user row blocking the pre-check.
        await using (var c = await PlatformFactory().OpenAsync())
            await c.ExecuteAsync(
                """DELETE FROM "dbo"."ContactClaims" WHERE "TenantId" = @t AND "NormalizedValue" = @v""",
                new { t = tid, v = email });

        var retry = await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Invite_with_a_fresh_contact_records_a_user_claim_in_the_ledger()
    {
        var tid = await SeedActiveTenant();
        var email = $"fresh{Guid.NewGuid():N}@x.com";
        await using var app = App();
        var admin = AdminClient(app, tid);

        var res = await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var newUserId = doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var claims = await ClaimsAsync(tid);
        claims.Should().ContainSingle(r =>
            r.Kind == "email" && r.NormalizedValue == email && r.OwnerType == "user" && r.OwnerId == newUserId.ToString());
    }

    [Fact]
    public async Task Second_invite_of_the_same_email_is_still_rejected_by_the_precheck_409()
    {
        var tid = await SeedActiveTenant();
        var email = $"dup{Guid.NewGuid():N}@x.com";
        await using var app = App();
        var admin = AdminClient(app, tid);

        (await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
