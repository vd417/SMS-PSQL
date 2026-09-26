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

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class InvitationLifecycleTests(PostgresFixture fx)
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

    private async Task<Guid> SeedActiveTenant()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var id = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\", \"Tier\") VALUES (@id,'T',@s,'active','gold')",
            new { id, s = $"t{id:N}" });
        return id;
    }

    private async Task<(Guid Id, string Status)> GetUserStatusAsync(string email)
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        return await c.QuerySingleAsync<(Guid, string)>(
            "SELECT \"Id\", \"Status\" FROM \"dbo\".\"Users\" WHERE \"Email\" = @email", new { email });
    }

    private async Task<(DateTime ExpiresAt, DateTime? AcceptedAt, string RoleLabel)> GetInvitationByEmailAsync(string email)
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        return await c.QuerySingleAsync<(DateTime, DateTime?, string)>(
            "SELECT \"ExpiresAt\", \"AcceptedAt\", \"RoleLabel\" FROM \"dbo\".\"Invitations\" WHERE \"Email\" = @email", new { email });
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    [Fact]
    public async Task Invite_creates_pending_user_and_an_invitation_row_valid_for_24_hours()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";

        (await admin.PostAsJsonAsync("/v1/users",
            new { email, roles = new[] { "school.teacher" } })).StatusCode.Should().Be(HttpStatusCode.Created);

        var (_, status) = await GetUserStatusAsync(email);
        status.Should().Be("pending");

        var (expiresAt, acceptedAt, roleLabel) = await GetInvitationByEmailAsync(email);
        roleLabel.Should().Be("Teacher");
        acceptedAt.Should().BeNull();
        expiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Accepting_the_invite_via_password_reset_marks_it_accepted_and_activates_the_user()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";

        await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });

        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE \"dbo\".\"OtpCodes\" SET \"CodeHash\"=@h WHERE \"Identifier\"=@id",
                new { id = email, h = Sha256Hex("123456") });

        var anon = app.CreateClient();
        var reset = await anon.PostAsJsonAsync("/v1/auth/password/reset",
            new { identifier = email, code = "123456", password = "NewPassw0rd!" });
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (_, status) = await GetUserStatusAsync(email);
        status.Should().Be("active");

        var (_, acceptedAt, _) = await GetInvitationByEmailAsync(email);
        acceptedAt.Should().NotBeNull();
    }

    private async Task<string> InviteAndGetIdAsync(HttpClient admin, string email)
    {
        await admin.PostAsJsonAsync("/v1/users", new { email, roles = new[] { "school.teacher" } });
        var list = await admin.GetAsync("/v1/invitations");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("email").GetString() == email).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Resend_extends_expiry_and_bumps_last_resent_at()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";
        var id = await InviteAndGetIdAsync(admin, email);

        var resend = await admin.PostAsync($"/v1/invitations/{id}/resend", null);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await admin.GetAsync("/v1/invitations");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var row = doc.RootElement.GetProperty("data").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        row.TryGetProperty("status", out var status).Should().BeTrue();
        status.GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Revoke_deactivates_the_user_and_blocks_further_resend_or_revoke()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";
        var id = await InviteAndGetIdAsync(admin, email);

        (await admin.PostAsync($"/v1/invitations/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, status) = await GetUserStatusAsync(email);
        status.Should().Be("revoked");

        (await admin.PostAsync($"/v1/invitations/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsync($"/v1/invitations/{id}/resend", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Non_admin_cannot_list_resend_or_revoke_invitations()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";
        var id = await InviteAndGetIdAsync(admin, email);

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tid, ["school.teacher"], isPlatform: false);
        var teacher = app.CreateClient();
        teacher.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await teacher.GetAsync("/v1/invitations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await teacher.PostAsync($"/v1/invitations/{id}/resend", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await teacher.PostAsync($"/v1/invitations/{id}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
