using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Auth;

/// A teacher onboarded into dbo.Teachers (no Users row, no invite) must be able to
/// self-serve "create your password": entering their email in forgot-password
/// materializes a school.teacher login on demand and sends the OTP — the same model
/// staff/student/parent already use. Invites stay reserved for CRM roles.
[Collection("sql")]
public class TeacherSelfServeLoginTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Onboarded_teacher_with_no_login_can_self_serve_create_password()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var email = $"teach{Guid.NewGuid():N}@school.test";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"Email\", \"Status\") " +
                "VALUES (@teacherId, @tenantId, 'Rina Verma', @email, 'active');",
                new { teacherId, tenantId, email });
        }

        // First-time / forgot: the teacher enters their email; a login is provisioned on demand.
        var anon = app.CreateClient();
        var forgot = await Data(await anon.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = email }), HttpStatusCode.OK);
        forgot.GetProperty("channel").GetString().Should().Be("email");
        forgot.GetProperty("recipient").GetString().Should().Be("self");
        forgot.GetProperty("sent_to").GetString().Should().Contain("@school.test");

        // A school.teacher login now exists (MustSetPassword), linked to the teacher profile.
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");

            var userId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT \"Id\" FROM \"dbo\".\"Users\" WHERE lower(\"Email\") = lower(@email)", new { email });
            userId.Should().NotBeNull("the teacher's login should be materialized by forgot-password");

            var mustSet = await conn.QuerySingleAsync<bool>(
                "SELECT \"MustSetPassword\" FROM \"dbo\".\"Users\" WHERE \"Id\" = @userId", new { userId });
            mustSet.Should().BeTrue();

            var role = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT \"Role\" FROM \"dbo\".\"UserRoles\" WHERE \"UserId\" = @userId", new { userId });
            role.Should().Be("school.teacher");

            // Key safety property: no password is set — the only way to complete is the OTP
            // sent to the teacher's own email. Materializing the row is not itself a login.
            var hasPassword = await conn.QuerySingleAsync<bool>(
                "SELECT \"PasswordHash\" IS NOT NULL FROM \"dbo\".\"Users\" WHERE \"Id\" = @userId", new { userId });
            hasPassword.Should().BeFalse();

            var linked = await conn.QuerySingleAsync<bool>(
                "SELECT \"UserId\" IS NOT NULL FROM \"dbo\".\"Teachers\" WHERE \"Id\" = @teacherId", new { teacherId });
            linked.Should().BeTrue("Teachers.UserId should be linked to the new login");
        }
    }

    [Fact]
    public async Task Inactive_teacher_is_not_provisioned()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var email = $"gone{Guid.NewGuid():N}@school.test";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"Email\", \"Status\") " +
                "VALUES (@teacherId, @tenantId, 'Ex Teacher', @email, 'inactive');",
                new { teacherId, tenantId, email });
        }

        var anon = app.CreateClient();
        var res = await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = email });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, await res.Content.ReadAsStringAsync());

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            var count = await conn.QuerySingleAsync<long>(
                "SELECT count(*) FROM \"dbo\".\"Users\" WHERE lower(\"Email\") = lower(@email)", new { email });
            count.Should().Be(0, "an inactive teacher must not get a login");
        }
    }
}
