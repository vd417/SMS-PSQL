using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Sms.Migrations;

namespace Sms.Tests.Integration.Migrations;

[Collection("sql")]
public class MigrationIdempotenceTests(PostgresFixture fx)
{
    [Fact]
    public async Task Rerunning_migrations_is_a_noop_and_login_procs_exist()
    {
        // The fixture already migrated this throwaway DB in InitializeAsync; a second run must be a no-op.
        var act = () => MigrationRunner.Run(fx.ConnectionString);
        act.Should().NotThrow();

        await using var c = new SqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var procs = (await c.QueryAsync<string>(
            "SELECT name FROM sys.procedures WHERE name IN ('User_GetById','UserRoles_GetByUser')")).ToList();
        procs.Should().Contain("User_GetById").And.Contain("UserRoles_GetByUser");
    }
}
