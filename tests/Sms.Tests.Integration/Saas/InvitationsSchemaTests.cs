using Dapper;
using FluentAssertions;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class InvitationsSchemaTests(PostgresFixture fx)
{
    [Fact]
    public async Task Invitations_Create_proc_inserts_a_row_and_returns_its_id()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Tenants\" (\"Id\", \"Name\", \"Slug\", \"Status\", \"Tier\") VALUES (@id,'T',@s,'active','gold')",
            new { id = tenantId, s = $"t{tenantId:N}" });
        await c.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"IsPlatform\", \"Status\") VALUES (@id,@tid,@email,false,'pending')",
            new { id = userId, tid = tenantId, email = "invitee@x.com" });

        var expiresAt = DateTime.UtcNow.AddHours(24);
        var id = await c.QuerySingleAsync<Guid>(
            new CommandDefinition(
                "SELECT * FROM dbo.invitations_create(" +
                "tenantid => @TenantId, userid => @UserId, email => @Email, phone => @Phone, " +
                "rolelabel => @RoleLabel, invitedbyuserid => @InvitedByUserId, expiresat => @ExpiresAt)",
                new
                {
                    TenantId = tenantId, UserId = userId, Email = "invitee@x.com", Phone = (string?)null,
                    RoleLabel = "Teacher", InvitedByUserId = (Guid?)null, ExpiresAt = expiresAt,
                }));

        var row = await c.QuerySingleAsync<(Guid Id, Guid UserId, string RoleLabel)>(
            "SELECT \"Id\", \"UserId\", \"RoleLabel\" FROM \"dbo\".\"Invitations\" WHERE \"Id\" = @id", new { id });
        row.UserId.Should().Be(userId);
        row.RoleLabel.Should().Be("Teacher");
    }
}
