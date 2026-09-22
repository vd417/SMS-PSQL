using Dapper;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Auth;

[Collection("sql")]
public class UserDirectoryRepositoryTests(PostgresFixture fx)
{
    // Seeding bypasses RLS the same way TestTenancy/AiConversationContextStoreTests do: a platform
    // (IsPlatform: true) TenantContext, so an arbitrary TenantId can be inserted directly.
    private async Task Seed(Func<System.Data.Common.DbConnection, Task> work)
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var conn = await factory.OpenAsync();
        await work(conn);
    }

    private static UserDirectoryRepository MakeRepo(string connectionString, Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, Guid.NewGuid(), false);
        var factory = new NpgsqlConnectionFactory(connectionString, ctx);
        return new UserDirectoryRepository(factory);
    }

    [Fact]
    public async Task SearchByNameAsync_finds_an_admin_by_name_and_reports_the_role_as_type()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"Name\", \"Status\") VALUES (@userId, @tenantId, @email, @name, 'active')",
            new { userId, tenantId, email = $"owner{Guid.NewGuid():N}@school.test", name = "Rahul Sharma" }));
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@userId, 'school.owner')", new { userId }));

        var repo = MakeRepo(fx.ConnectionString, tenantId);

        var matches = await repo.SearchByNameAsync("Rahul");

        matches.Should().ContainSingle(m => m.Id == userId && m.Name == "Rahul Sharma" && m.Type == "owner");
    }

    [Fact]
    public async Task SearchByNameAsync_never_matches_a_row_with_no_Name_set()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"Status\") VALUES (@userId, @tenantId, @email, 'active')",
            new { userId, tenantId, email = $"noname{Guid.NewGuid():N}@school.test" }));
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@userId, 'school.admin')", new { userId }));

        var repo = MakeRepo(fx.ConnectionString, tenantId);

        (await repo.SearchByNameAsync("anything")).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchByNameAsync_never_returns_a_student_parent_or_teacher_role_only_account()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"Name\", \"Status\") VALUES (@userId, @tenantId, @email, 'Rahul Sharma', 'active')",
            new { userId, tenantId, email = $"teacher{Guid.NewGuid():N}@school.test" }));
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@userId, 'school.teacher')", new { userId }));

        var repo = MakeRepo(fx.ConnectionString, tenantId);

        (await repo.SearchByNameAsync("Rahul")).Should().BeEmpty(
            "a school.teacher-only account is directory data for PersonResolver's Teachers-table branch, not the Users branch");
    }

    [Fact]
    public async Task GetByIdAsync_returns_the_current_name_for_a_known_admin_like_id()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"Name\", \"Status\") VALUES (@userId, @tenantId, @email, @name, 'active')",
            new { userId, tenantId, email = $"principal{Guid.NewGuid():N}@school.test", name = "Priya Singh" }));
        await Seed(conn => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@userId, 'school.principal')", new { userId }));

        var repo = MakeRepo(fx.ConnectionString, tenantId);

        var match = await repo.GetByIdAsync(userId);

        match.Should().NotBeNull();
        match!.Name.Should().Be("Priya Singh");
        match.Type.Should().Be("principal");
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_for_an_unknown_id()
    {
        var tenantId = Guid.NewGuid();
        var repo = MakeRepo(fx.ConnectionString, tenantId);

        (await repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }
}
