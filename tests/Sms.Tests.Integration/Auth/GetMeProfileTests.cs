using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class GetMeProfileTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> AppWithDb() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    [Fact]
    public async Task Teacher_me_returns_name_and_title_from_linked_Teachers_row()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Jane Teacher')",
                new { userId, tenantId, email = $"t{Guid.NewGuid():N}@x.com", hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (TenantId, Name, Designation, UserId) VALUES (@tenantId, 'Jane Teacher', 'Senior Teacher', @userId)",
                new { tenantId, userId });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'school.teacher')", new { userId });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.teacher" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("Jane Teacher");
        data.GetProperty("title").GetString().Should().Be("Senior Teacher");
    }

    [Fact]
    public async Task Principal_me_returns_name_but_null_title()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Priya Principal')",
                new { userId, tenantId, email = $"p{Guid.NewGuid():N}@x.com", hash = hasher.Hash("Pass123!") });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.principal" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("Priya Principal");
        data.GetProperty("title").GetString().Should().Be("Principal");
    }

    [Fact]
    public async Task Teacher_me_returns_tenant_tier_and_plan_name()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Tenants (Id, Name, Slug, Status, Tier, PlanName) VALUES (@tenantId, 'Gold Academy', @slug, 'active', 'gold', 'Gold')",
                new { tenantId, slug = $"t{tenantId:N}" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Gold Teacher')",
                new { userId, tenantId, email = $"g{Guid.NewGuid():N}@x.com", hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'school.teacher')", new { userId });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.teacher" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("tier").GetString().Should().Be("gold");
        data.GetProperty("plan_name").GetString().Should().Be("Gold");
        data.GetProperty("tenant_name").GetString().Should().Be("Gold Academy");
    }

    [Fact]
    public async Task Teacher_me_returns_contact_fields_from_linked_Teachers_row()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "contact@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name, Phone) VALUES (@userId, @tenantId, @email, @hash, 'Jane Teacher', '9000000001')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (TenantId, Name, Designation, UserId, Email, Phone, EmployeeCode, ClassTeacher, CreatedAt) " +
                "VALUES (@tenantId, 'Jane Teacher', 'Senior Teacher', @userId, @email, '9000000099', 'TCH-42', 'IX-A', SYSUTCDATETIME())",
                new { tenantId, userId, email });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userId, 'school.teacher')", new { userId });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.teacher" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("email").GetString().Should().Be(email);
        data.GetProperty("phone").GetString().Should().Be("9000000001");
        data.GetProperty("employee").GetString().Should().Be("TCH-42");
        data.GetProperty("classroom").GetString().Should().Be("IX-A");
        data.GetProperty("joined").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Principal_me_resolves_employee_from_staff_row_linked_by_email()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string email = "principal@x.com";
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userId, @tenantId, @email, @hash, 'Priya Principal')",
                new { userId, tenantId, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Staff (TenantId, Name, Role, Email, Phone, EmployeeCode, CreatedAt) " +
                "VALUES (@tenantId, 'Priya Principal', 'Principal', @email, '9111111111', 'STF-7', SYSUTCDATETIME())",
                new { tenantId, email });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { "school.principal" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("title").GetString().Should().Be("Principal");
        data.GetProperty("phone").GetString().Should().Be("9111111111");
        data.GetProperty("employee").GetString().Should().Be("STF-7");
    }

    [Fact]
    public async Task Me_returns_shared_phone_from_roster_in_another_school()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        const string email = "shared-phone@x.com";
        const string phone = "7388119922";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                "INSERT dbo.Tenants (Id, Name, Slug) VALUES (@tenantA, 'School A', @slugA), (@tenantB, 'School B', @slugB)",
                new { tenantA, tenantB, slugA = $"a{tenantA:N}", slugB = $"b{tenantB:N}" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userA, @tenantA, @email, @hash, 'Rina A'), (@userB, @tenantB, @email, @hash, NULL)",
                new { userA, userB, tenantA, tenantB, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (TenantId, Name, Designation, UserId, Email, Phone, EmployeeCode, CreatedAt) " +
                "VALUES (@tenantA, 'Rina A', 'Teacher', @userA, @email, @phone, 'TCH-1', SYSUTCDATETIME())",
                new { tenantA, userA, email, phone });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userA, 'school.teacher'), (@userB, 'school.teacher')",
                new { userA, userB });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(userB, tenantB, new[] { "school.teacher" }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/auth/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("phone").GetString().Should().Be(phone);
    }

    [Fact]
    public async Task Switch_school_copies_shared_phone_onto_target_user()
    {
        var hasher = new Sms.Shared.Kernel.Auth.PasswordHasher();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        const string email = "switch-phone@x.com";
        const string phone = "9000001234";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                "INSERT dbo.Tenants (Id, Name, Slug) VALUES (@tenantA, 'School A', @slugA), (@tenantB, 'School B', @slugB)",
                new { tenantA, tenantB, slugA = $"a{tenantA:N}", slugB = $"b{tenantB:N}" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, PasswordHash, Name) VALUES (@userA, @tenantA, @email, @hash, 'Rina A'), (@userB, @tenantB, @email, @hash, NULL)",
                new { userA, userB, tenantA, tenantB, email, hash = hasher.Hash("Pass123!") });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (TenantId, Name, Designation, UserId, Email, Phone, EmployeeCode, CreatedAt) " +
                "VALUES (@tenantA, 'Rina A', 'Teacher', @userA, @email, @phone, 'TCH-9', SYSUTCDATETIME())",
                new { tenantA, userA, email, phone });
            await conn.ExecuteAsync(
                "INSERT dbo.UserRoles (UserId, Role) VALUES (@userA, 'school.teacher'), (@userB, 'school.teacher')",
                new { userA, userB });
        }

        await using var app = AppWithDb();
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = "integration-test-signing-key-32-bytes-min!!", AccessTokenMinutes = 15 },
            new Sms.Shared.Kernel.Time.SystemClock());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            jwt.IssueAccess(userA, tenantA, new[] { "school.teacher" }, isPlatform: false));

        var switchRes = await client.PostAsJsonAsync("/v1/me/switch-school", new { tenant_id = tenantB });
        switchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var switchDoc = JsonDocument.Parse(await switchRes.Content.ReadAsStringAsync());
        var access = switchDoc.RootElement.GetProperty("data").GetProperty("access_token").GetString()!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantB.ToString());

        var meRes = await client.GetAsync("/v1/auth/me");
        using var meDoc = JsonDocument.Parse(await meRes.Content.ReadAsStringAsync());
        meDoc.RootElement.GetProperty("data").GetProperty("phone").GetString().Should().Be(phone);

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            var stored = await conn.QuerySingleAsync<string?>(
                "SELECT Phone FROM dbo.Users WHERE Id = @userB", new { userB });
            stored.Should().Be(phone);
        }
    }
}
