using Dapper;
using FluentAssertions;
using Npgsql;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.DAO;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

// B4: the C# boundary over dbo.contact_claims_sync (0011). Verifies the DAO maps the SQL
// contract's SMSDC sentinel ("contact_conflict:<kind>") to a normal result value, and that
// any other database error propagates untouched. Runs under a real tenant-scoped connection so
// the ContactClaims RLS policies are genuinely exercised, exactly as a tenant caller would hit it.
[Collection("sql")]
public class ContactValidatorTests(PostgresFixture fx)
{
    private ContactValidator Validator(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, null, isPlatform: false);
        return new ContactValidator(new NpgsqlConnectionFactory(fx.ConnectionString, ctx));
    }

    private sealed record ClaimRow(
        string Kind, string NormalizedValue, string OwnerType, string OwnerId, Guid? PersonId, Guid TenantId);

    private async Task<IReadOnlyList<ClaimRow>> ClaimsAsync(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, null, isPlatform: false);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        var rows = await c.QueryAsync<ClaimRow>(
            """
            SELECT "Kind", "NormalizedValue", "OwnerType", "OwnerId", "PersonId", "TenantId"
            FROM "dbo"."ContactClaims" WHERE "TenantId" = @t ORDER BY "Kind"
            """,
            new { t = tenantId });
        return rows.AsList();
    }

    [Fact]
    public async Task No_conflict_returns_a_valid_result()
    {
        var tenant = Guid.NewGuid();

        var result = await Validator(tenant).SyncAsync(tenant, "user", "U1", Guid.NewGuid(), "a@x.com", null);

        result.IsValid.Should().BeTrue();
        result.ConflictKind.Should().BeNull();
    }

    [Fact]
    public async Task Email_claimed_by_a_different_person_returns_an_email_conflict()
    {
        var tenant = Guid.NewGuid();
        var v = Validator(tenant);
        (await v.SyncAsync(tenant, "user", "U1", Guid.NewGuid(), "a@x.com", null)).IsValid.Should().BeTrue();

        var result = await v.SyncAsync(tenant, "user", "U2", Guid.NewGuid(), "a@x.com", null);

        result.IsValid.Should().BeFalse();
        result.ConflictKind.Should().Be(ContactConflictKind.Email);
    }

    [Fact]
    public async Task Phone_claimed_by_a_different_person_returns_a_phone_conflict()
    {
        var tenant = Guid.NewGuid();
        var v = Validator(tenant);
        (await v.SyncAsync(tenant, "user", "U1", Guid.NewGuid(), null, "+91 98765 43210")).IsValid.Should().BeTrue();

        var result = await v.SyncAsync(tenant, "user", "U2", Guid.NewGuid(), null, "9876543210");

        result.IsValid.Should().BeFalse();
        result.ConflictKind.Should().Be(ContactConflictKind.Phone);
    }

    [Fact]
    public async Task An_unexpected_database_error_is_not_swallowed_as_a_conflict()
    {
        var tenant = Guid.NewGuid();
        // OwnerId longer than ContactClaims.OwnerId varchar(64) forces a genuine string-truncation
        // error (SQLSTATE 22001) from the INSERT inside contact_claims_sync. That is NOT the SMSDC
        // business sentinel, so it must propagate as-is, never be turned into a contact conflict.
        var overlongOwnerId = new string('x', 100);

        var act = () => Validator(tenant).SyncAsync(tenant, "user", overlongOwnerId, Guid.NewGuid(), "fresh@x.com", null);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task Passes_tenant_owner_person_email_and_phone_through_to_the_sync_function()
    {
        var tenant = Guid.NewGuid();
        var person = Guid.NewGuid();

        var result = await Validator(tenant)
            .SyncAsync(tenant, "teacher", "T7", person, "Test@Example.COM", "+91 98765 43210");

        result.IsValid.Should().BeTrue();
        var claims = await ClaimsAsync(tenant);
        claims.Should().HaveCount(2);
        claims.Should().AllSatisfy(r =>
        {
            r.TenantId.Should().Be(tenant);
            r.OwnerType.Should().Be("teacher");
            r.OwnerId.Should().Be("T7");
            r.PersonId.Should().Be(person);
        });
        claims.Single(r => r.Kind == "email").NormalizedValue.Should().Be("test@example.com");
        claims.Single(r => r.Kind == "phone").NormalizedValue.Should().Be("9876543210");
    }
}
