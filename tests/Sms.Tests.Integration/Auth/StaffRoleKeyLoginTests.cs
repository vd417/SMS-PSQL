using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sms.Tests.Integration.Auth;

/// <summary>
/// Covers the authoritative role_key/duty_post fields on GET /v1/auth/me — sourced from the
/// authenticated user's own dbo.Staff row, never from client input.
/// </summary>
[Collection("sql")]
public class StaffRoleKeyLoginTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> AppWithDb() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static Sms.Shared.Kernel.Auth.JwtTokenService NewJwt() => new(
        new Sms.Shared.Kernel.Auth.JwtOptions
        {
            Issuer = "sms", Audience = "sms-apps",
            SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15,
        },
        new Sms.Shared.Kernel.Time.SystemClock());

    [Fact]
    public async Task Driver_logs_in_and_me_returns_role_key_driver()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "driver@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Dave Driver')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (TenantId, Name, Role, Email, Phone, Route, UserId, CreatedAt) " +
                "VALUES (@tenantId, 'Dave Driver', 'Driver', @email, '9000000011', 'Route 12', @userId, SYSUTCDATETIME())",
                new { tenantId, email, userId });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'staff')", new { userId });
        }

        await using var app = AppWithDb();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            NewJwt().IssueAccess(userId, tenantId, new[] { "staff" }, isPlatform: false));

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("role_key").GetString().Should().Be("driver");
        data.GetProperty("duty_post").GetString().Should().Be("Route 12");
    }

    [Fact]
    public async Task Watchman_staff_role_maps_to_canonical_guard_key()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "watchman@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Gopal Guard')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (TenantId, Name, Role, Email, Phone, Department, UserId, CreatedAt) " +
                "VALUES (@tenantId, 'Gopal Guard', 'Watchman', @email, '9000000022', 'Main Gate', @userId, SYSUTCDATETIME())",
                new { tenantId, email, userId });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'staff')", new { userId });
        }

        await using var app = AppWithDb();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            NewJwt().IssueAccess(userId, tenantId, new[] { "staff" }, isPlatform: false));

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("role_key").GetString().Should().Be("guard");
        // No Route on file — falls back to Department.
        data.GetProperty("duty_post").GetString().Should().Be("Main Gate");
    }

    [Fact]
    public async Task Teacher_without_staff_row_logs_in_with_no_role_key()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "teacher-noduty@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Tara Teacher')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (TenantId, Name, Designation, UserId, Email, CreatedAt) " +
                "VALUES (@tenantId, 'Tara Teacher', 'Senior Teacher', @userId, @email, SYSUTCDATETIME())",
                new { tenantId, email, userId });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'school.teacher')", new { userId });
        }

        await using var app = AppWithDb();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            NewJwt().IssueAccess(userId, tenantId, new[] { "school.teacher" }, isPlatform: false));

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("role_key").ValueKind.Should().Be(JsonValueKind.Null);
        data.GetProperty("duty_post").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Unrecognized_staff_role_label_logs_in_without_crashing_and_no_role_key()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "mystery-staff@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Mystery Staffer')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (TenantId, Name, Role, Email, Phone, UserId, CreatedAt) " +
                "VALUES (@tenantId, 'Mystery Staffer', 'Storekeeper', @email, '9000000033', @userId, SYSUTCDATETIME())",
                new { tenantId, email, userId });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'staff')", new { userId });
        }

        await using var app = AppWithDb();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            NewJwt().IssueAccess(userId, tenantId, new[] { "staff" }, isPlatform: false));

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("role_key").ValueKind.Should().Be(JsonValueKind.Null);
        // Still has a duty post — Staff row exists, it's the role label that's unrecognized.
        data.GetProperty("duty_post").GetString().Should().Be("");
    }
}
