using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class ParentDevicesSchemaTests(PostgresFixture fx)
{
    private static async Task<NpgsqlConnection> Open(string cs, Guid tenantId)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return conn;
    }

    [Fact]
    public async Task Rows_are_tenant_isolated_by_rls()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";

        await using (var a = await Open(fx.ConnectionString, tenantA))
            await a.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'ios')",
                new { t = tenantA, u = Guid.NewGuid(), k = token });

        await using var b = await Open(fx.ConnectionString, tenantB);
        var visibleToB = await b.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"ParentDevices\" WHERE \"ExpoPushToken\" = @k", new { k = token });
        visibleToB.Should().Be(0, "tenant B must not see tenant A's device rows");
    }

    [Fact]
    public async Task Same_token_twice_in_one_tenant_violates_the_unique_index()
    {
        var tenantId = Guid.NewGuid();
        var token = $"ExponentPushToken[{Guid.NewGuid():N}]";
        await using var conn = await Open(fx.ConnectionString, tenantId);
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'ios')",
            new { t = tenantId, u = Guid.NewGuid(), k = token });

        var act = () => conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"ParentDevices\" (\"TenantId\",\"UserId\",\"ExpoPushToken\",\"Platform\") VALUES (@t,@u,@k,'android')",
            new { t = tenantId, u = Guid.NewGuid(), k = token });

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }
}
