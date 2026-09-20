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

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class ProvisioningTests(PostgresFixture fx)
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
            """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@id,'T',@s,'active','gold')""",
            new { id, s = $"t{id:N}" });
        return id;
    }

    [Fact]
    public async Task Invite_user_then_that_user_can_otp_login_with_role()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var email = $"teacher{Guid.NewGuid():N}@x.com";

        (await admin.PostAsJsonAsync("/v1/users",
            new { email, roles = new[] { "school.teacher" } })).StatusCode.Should().Be(HttpStatusCode.Created);

        // OTP login as the invited user (overwrite the code hash with a known value).
        var anon = app.CreateClient();
        (await anon.PostAsJsonAsync("/v1/auth/otp/request", new { identifier = email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync("UPDATE dbo.OtpCodes SET CodeHash=@h WHERE Identifier=@id",
                new { id = email, h = Sha256Hex("123456") });

        var verify = await anon.PostAsJsonAsync("/v1/auth/otp/verify", new { identifier = email, code = "123456" });
        using var doc = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("data").GetProperty("access_token").GetString();
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Bulk_import_creates_users_skips_duplicates_and_reports_errors()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var admin = AdminClient(app, tid);
        var e1 = $"a{Guid.NewGuid():N}@x.com";
        var e2 = $"b{Guid.NewGuid():N}@x.com";

        var res = await admin.PostAsJsonAsync("/v1/users/import", new
        {
            rows = new[]
            {
                new { email = (string?)e1, phone = (string?)null, role = "school.teacher" },
                new { email = (string?)e2, phone = (string?)null, role = "student.parent" },
                new { email = (string?)e1, phone = (string?)null, role = "school.teacher" },       // duplicate -> skipped
                new { email = (string?)null, phone = (string?)null, role = "school.teacher" }, // invalid -> error
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("created").GetInt32().Should().Be(2);
        data.GetProperty("skipped").GetInt32().Should().Be(1);
        var errors = data.GetProperty("errors");
        errors.GetArrayLength().Should().Be(1);
        errors[0].GetProperty("row").GetInt32().Should().Be(3); // the 4th row (index 3) was invalid
    }

    [Fact]
    public async Task Non_admin_cannot_invite()
    {
        var tid = await SeedActiveTenant();
        await using var app = App();
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tid, ["school.teacher"], isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await c.PostAsJsonAsync("/v1/users", new { email = "x@y.com", roles = new[] { "school.teacher" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
}
