using Dapper;
using FluentAssertions;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.DAO;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class InvitationDaoTests(PostgresFixture fx)
{
    private async Task<(Guid TenantId, Guid UserId)> SeedTenantAndUserAsync()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@id,'T',@s,'active','gold')""",
            new { id = tenantId, s = $"t{tenantId:N}" });
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "IsPlatform", "Status") VALUES (@id,@tid,'invitee@x.com',false,'pending')""",
            new { id = userId, tid = tenantId });
        return (tenantId, userId);
    }

    private IInvitationDao Dao()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        return new InvitationDao(new NpgsqlConnectionFactory(fx.ConnectionString, ctx));
    }

    [Fact]
    public async Task Create_then_list_then_resend_then_revoke_round_trips()
    {
        var (tenantId, userId) = await SeedTenantAndUserAsync();
        var dao = Dao();

        var id = await dao.CreateAsync(tenantId, userId, "invitee@x.com", null, "Teacher",
            null, DateTime.UtcNow.AddHours(24));

        var listed = await dao.ListByTenantAsync(tenantId);
        listed.Should().ContainSingle(r => r.Id == id && r.RoleLabel == "Teacher" && r.AcceptedAt == null && r.RevokedAt == null);

        var newExpiry = DateTime.UtcNow.AddHours(48);
        await dao.MarkResentAsync(id, newExpiry);
        var afterResend = await dao.GetByIdAsync(tenantId, id);
        afterResend!.LastResentAt.Should().NotBeNull();
        afterResend.ExpiresAt.Should().BeCloseTo(newExpiry, TimeSpan.FromSeconds(2));

        await dao.MarkRevokedAsync(id);
        var afterRevoke = await dao.GetByIdAsync(tenantId, id);
        afterRevoke!.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkAcceptedByUserId_is_a_noop_when_already_revoked()
    {
        var (tenantId, userId) = await SeedTenantAndUserAsync();
        var dao = Dao();
        var id = await dao.CreateAsync(tenantId, userId, "invitee@x.com", null, "Teacher", null, DateTime.UtcNow.AddHours(24));
        await dao.MarkRevokedAsync(id);

        await dao.MarkAcceptedByUserIdAsync(userId);

        var row = await dao.GetByIdAsync(tenantId, id);
        row!.AcceptedAt.Should().BeNull();
    }
}
