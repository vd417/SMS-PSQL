using Dapper;
using FluentAssertions;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Data;

[Collection("sql")]
public class RlsIsolationTests(PostgresFixture fx)
{
    [Fact]
    public async Task Tenant_session_sees_only_its_own_users()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var platform = new TenantContext();
        platform.Set(null, Guid.NewGuid(), isPlatform: true);
        var platformFactory = new NpgsqlConnectionFactory(fx.ConnectionString, platform);
        await using (var seed = await platformFactory.OpenAsync())
        {
            await seed.ExecuteAsync(
                "INSERT dbo.Tenants (Id, Name, Slug) VALUES (@a,'A',@sa),(@b,'B',@sb)",
                new { a = tenantA, b = tenantB, sa = "a-" + tenantA.ToString("N"), sb = "b-" + tenantB.ToString("N") });
            await seed.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email) VALUES (NEWID(),@a,'a@x.com'),(NEWID(),@b,'b@x.com')",
                new { a = tenantA, b = tenantB });
        }

        var aCtx = new TenantContext();
        aCtx.Set(tenantA, Guid.NewGuid(), isPlatform: false);
        var aFactory = new NpgsqlConnectionFactory(fx.ConnectionString, aCtx);
        await using var connA = await aFactory.OpenAsync();
        var emails = await connA.QueryAsync<string>("SELECT Email FROM dbo.Users");
        emails.Should().ContainSingle().Which.Should().Be("a@x.com");
    }
}
