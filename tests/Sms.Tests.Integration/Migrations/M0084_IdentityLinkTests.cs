using System.Data;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Sms.Tests.Integration.Migrations;

[Collection("sql")]
public class M0084_IdentityLinkTests(PostgresFixture fx)
{
    [Fact]
    public async Task Users_Teachers_Staff_have_new_columns()
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var userCols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Users'")).ToList();
        userCols.Should().Contain("Name").And.Contain("MustSetPassword");

        var teacherCols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Teachers'")).ToList();
        teacherCols.Should().Contain("UserId");

        var staffCols = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Staff'")).ToList();
        staffCols.Should().Contain("UserId");
    }

    [Fact]
    public async Task Teachers_UserId_unique_index_rejects_duplicate_link()
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Set SESSION_CONTEXT for RLS
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@v", new { v = tenantId });
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=@v", new { v = 0 });

        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId) VALUES (@userId, @tenantId)", new { userId, tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (TenantId, Name, UserId) VALUES (@tenantId, 'A', @userId)", new { tenantId, userId });

        var act = () => conn.ExecuteAsync(
            "INSERT dbo.Teachers (TenantId, Name, UserId) VALUES (@tenantId, 'B', @userId)", new { tenantId, userId });

        await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
    }
}
