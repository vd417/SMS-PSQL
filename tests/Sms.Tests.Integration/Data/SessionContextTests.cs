using Dapper;
using FluentAssertions;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Data;

[Collection("sql")]
public class SessionContextTests(PostgresFixture fx)
{
    [Fact]
    public async Task Open_stamps_tenant_id_into_session_context()
    {
        var tid = Guid.NewGuid();
        var ctx = new TenantContext();
        ctx.Set(tid, Guid.NewGuid(), isPlatform: false);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);

        await using var conn = await factory.OpenAsync();
        var read = await conn.QuerySingleAsync<Guid>(
            "SELECT current_setting('app.tenant_id')::uuid");
        read.Should().Be(tid);
    }
}
