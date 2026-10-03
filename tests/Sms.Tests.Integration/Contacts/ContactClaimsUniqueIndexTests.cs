using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

// B7: the gated UNIQUE index UX_ContactClaims_Tenant_Kind_Value on
// dbo."ContactClaims" ("TenantId","Kind","NormalizedValue"). It is the DB-level safety net
// behind contact_claims_sync (whose INSERT already tolerates unique_violation), closing the
// concurrent-race window where two writers could otherwise both claim the same contact.
[Collection("sql")]
public class ContactClaimsUniqueIndexTests(PostgresFixture fx)
{
    private NpgsqlConnectionFactory Platform()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private static Task InsertAsync(System.Data.Common.DbConnection c, Guid tenant, string kind, string value, string ownerId) =>
        c.ExecuteAsync(
            """
            INSERT INTO "dbo"."ContactClaims" ("TenantId", "Kind", "NormalizedValue", "OwnerType", "OwnerId")
            VALUES (@t, @k, @v, 'user', @o)
            """,
            new { t = tenant, k = kind, v = value, o = ownerId });

    [Fact]
    public async Task Duplicate_tenant_kind_and_value_violates_the_unique_index()
    {
        var tenant = Guid.NewGuid();
        await using var c = await Platform().OpenAsync();
        await InsertAsync(c, tenant, "email", "dup@x.com", "A");

        var act = () => InsertAsync(c, tenant, "email", "dup@x.com", "B");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Same_value_in_different_tenants_is_allowed()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        await using var c = await Platform().OpenAsync();
        await InsertAsync(c, t1, "email", "shared@x.com", "A");

        var act = () => InsertAsync(c, t2, "email", "shared@x.com", "B");

        await act.Should().NotThrowAsync(); // the index is per-tenant, not global
    }

    [Fact]
    public async Task Same_tenant_and_value_but_different_kind_is_allowed()
    {
        var tenant = Guid.NewGuid();
        await using var c = await Platform().OpenAsync();
        await InsertAsync(c, tenant, "email", "9999999999", "A");

        var act = () => InsertAsync(c, tenant, "phone", "9999999999", "B");

        await act.Should().NotThrowAsync(); // Kind is part of the composite key
    }
}
