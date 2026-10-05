using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

[Collection("sql")]
public class ContactClaimsSyncTests(PostgresFixture fx)
{
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid();

    // Opens a platform-scoped connection inside a transaction that is never committed.
    private async Task<(NpgsqlConnection C, NpgsqlTransaction Tx)> BeginAsync()
    {
        var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        var tx = await c.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.is_platform', '1', true)", c, tx);
        await cmd.ExecuteNonQueryAsync();
        return (c, tx);
    }

    private static NpgsqlParameter P(NpgsqlDbType t, object? v) =>
        new() { NpgsqlDbType = t, Value = v ?? DBNull.Value };

    private static async Task SyncAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, string ownerType,
        string ownerId, Guid? person, string? email, string? phone)
    {
        // savepoint so a raised conflict does not poison the surrounding transaction
        await using (var sp = new NpgsqlCommand("SAVEPOINT s", c, tx)) await sp.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT dbo.contact_claims_sync($1, $2, $3, $4, $5, $6)", c, tx);
            cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, ownerType));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, ownerId));
            cmd.Parameters.Add(P(NpgsqlDbType.Uuid, person));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, email));
            cmd.Parameters.Add(P(NpgsqlDbType.Text, phone));
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT s", c, tx);
            await rb.ExecuteNonQueryAsync();
            throw;
        }
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, string? value = null)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM dbo.\"ContactClaims\" WHERE \"TenantId\" = $1 AND ($2::text IS NULL OR \"NormalizedValue\" = $2)", c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, value));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ShouldConflictAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("SMSDC");

    [Fact]
    public async Task First_sync_creates_one_claim()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        (await CountAsync(c, tx, t)).Should().Be(1);
    }

    [Fact]
    public async Task Different_owner_and_person_conflicts()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        await ShouldConflictAsync(() => SyncAsync(c, tx, t, "user", "U2", P2, "a@x.com", null));
        (await CountAsync(c, tx, t)).Should().Be(1);
    }

    [Fact]
    public async Task Same_person_different_owner_reuses_claim()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        await SyncAsync(c, tx, t, "teacher", "T1", P1, "a@x.com", null);
        (await CountAsync(c, tx, t)).Should().Be(1);
    }

    [Fact]
    public async Task Same_owner_resync_is_idempotent()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        (await CountAsync(c, tx, t)).Should().Be(1);
    }

    [Fact]
    public async Task Changing_email_releases_old_claim()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        await SyncAsync(c, tx, t, "user", "U1", P1, "b@x.com", null);
        (await CountAsync(c, tx, t, "a@x.com")).Should().Be(0);
        (await CountAsync(c, tx, t, "b@x.com")).Should().Be(1);
        await SyncAsync(c, tx, t, "user", "U2", P2, "a@x.com", null);
        (await CountAsync(c, tx, t, "a@x.com")).Should().Be(1);
    }

    [Fact]
    public async Task Same_normalized_phone_for_different_people_conflicts()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, null, "+91 98765 43210");
        await ShouldConflictAsync(() => SyncAsync(c, tx, t, "user", "U2", P2, null, "9876543210"));
    }

    [Fact]
    public async Task Null_email_and_phone_is_a_noop()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", P1, null, null);
        (await CountAsync(c, tx, t)).Should().Be(0);
    }

    [Fact]
    public async Task Same_owner_adopts_person_id()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;
        await SyncAsync(c, tx, t, "user", "U1", null, "a@x.com", null);
        await SyncAsync(c, tx, t, "user", "U1", P1, "a@x.com", null);
        await using var cmd = new NpgsqlCommand(
            "SELECT \"PersonId\" FROM dbo.\"ContactClaims\" WHERE \"TenantId\" = $1", c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, t));
        ((Guid)(await cmd.ExecuteScalarAsync())!).Should().Be(P1);
        (await CountAsync(c, tx, t)).Should().Be(1);
    }
}
