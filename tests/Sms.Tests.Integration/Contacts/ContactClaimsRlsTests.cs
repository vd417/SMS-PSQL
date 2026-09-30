using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

[Collection("sql")]
public class ContactClaimsRlsTests(PostgresFixture fx)
{
    private async Task<NpgsqlConnection> OpenAsync(Guid? tenant, bool platform)
    {
        var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', $1, false), set_config('app.is_platform', $2, false)", c);
        cmd.Parameters.AddWithValue(tenant?.ToString() ?? "");
        cmd.Parameters.AddWithValue(platform ? "1" : "0");
        await cmd.ExecuteNonQueryAsync();
        return c;
    }

    private static async Task InsertAsync(NpgsqlConnection c, Guid tenant, string value)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO dbo.\"ContactClaims\" (\"TenantId\", \"Kind\", \"NormalizedValue\", \"OwnerType\", \"OwnerId\") " +
            "VALUES ($1, 'email', $2, 'user', 'u1')", c);
        cmd.Parameters.AddWithValue(tenant);
        cmd.Parameters.AddWithValue(value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, string value)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM dbo.\"ContactClaims\" WHERE \"NormalizedValue\" = $1", c);
        cmd.Parameters.AddWithValue(value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task TenantA_cannot_read_tenant_B_claim()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var value = $"b-{Guid.NewGuid():N}@x.com";
        await using (var plat = await OpenAsync(null, true)) await InsertAsync(plat, b, value);

        await using var conn = await OpenAsync(a, false);
        (await CountAsync(conn, value)).Should().Be(0);
    }

    [Fact]
    public async Task TenantA_can_insert_and_read_own_claim()
    {
        var a = Guid.NewGuid();
        var value = $"a-{Guid.NewGuid():N}@x.com";
        await using var conn = await OpenAsync(a, false);
        await InsertAsync(conn, a, value);
        (await CountAsync(conn, value)).Should().Be(1);
    }

    [Fact]
    public async Task TenantA_inserting_for_tenant_B_is_rejected_by_with_check()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await using var conn = await OpenAsync(a, false);
        var act = () => InsertAsync(conn, b, $"x-{Guid.NewGuid():N}@x.com");
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("42501"); // insufficient_privilege: new row violates row-level security
    }

    [Fact]
    public async Task Platform_can_insert_for_any_tenant()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var va = $"pa-{Guid.NewGuid():N}@x.com";
        var vb = $"pb-{Guid.NewGuid():N}@x.com";
        await using var conn = await OpenAsync(null, true);
        await InsertAsync(conn, a, va);
        await InsertAsync(conn, b, vb);
        (await CountAsync(conn, va)).Should().Be(1);
        (await CountAsync(conn, vb)).Should().Be(1);
    }
}
