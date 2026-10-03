using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Identity;

/// Regression tests for migration 0008 (Users.PersonId backfill, corrected rule): StudentId is NOT a
/// same-person merge key. Runs only against the fixture's disposable sms_test_* database. The 0008
/// statement is re-executed against freshly seeded rows (the migration itself ran on an empty table
/// at fixture setup); each test works in a rolled-back transaction so nothing leaks between tests.
[Collection("sql")]
public class PersonIdBackfillTests(PostgresFixture fx)
{
    private static string BackfillSql() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "migrations", "0009_users_personid_backfill.sql"));

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsync()
    {
        var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        // Platform elevation: cases span tenants and the backfill must see every tenant's rows.
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', true)", transaction: tx);
        return (conn, tx);
    }

    private static async Task<Guid> Seed(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, string? name,
        string? email, string? phone, string? studentId)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync("""
            INSERT INTO dbo."Users" ("Id","TenantId","Name","Email","Phone","StudentId","IsPlatform")
            VALUES (@id,@tenant,@name,@email,@phone,@studentId,false)
            """, new { id, tenant, name, email, phone, studentId }, tx);
        return id;
    }

    private static Task<Guid?> Pid(NpgsqlConnection c, NpgsqlTransaction tx, Guid id) =>
        c.QuerySingleAsync<Guid?>("SELECT \"PersonId\" FROM dbo.\"Users\" WHERE \"Id\"=@id", new { id }, tx);

    private static string U() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Student_login_gets_a_PersonId()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var student = await Seed(c, tx, Guid.NewGuid(), "Vaibhav S", $"vaibhav{U()}@school.test", null, "ADM-1");
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        (await Pid(c, tx, student)).Should().NotBeNull();
    }

    [Fact]
    public async Task Parent_login_gets_a_different_PersonId_from_the_student()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var t = Guid.NewGuid();
        var student = await Seed(c, tx, t, "Vaibhav S", $"vaibhav{U()}@school.test", null, "ADM-2");
        var parent = await Seed(c, tx, t, "Suresh S", $"suresh{U()}@home.test", "9000000002", "ADM-2");
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        var sp = await Pid(c, tx, student); var pp = await Pid(c, tx, parent);
        sp.Should().NotBeNull(); pp.Should().NotBeNull();
        pp!.Value.Should().NotBe(sp!.Value);
    }

    [Fact]
    public async Task Same_AdmissionNo_with_student_and_parent_logins_yields_distinct_PersonIds()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var t = Guid.NewGuid();
        // Rahul/Vaibhav case: identical StudentId, different people; a blank parent name must not merge either.
        var student = await Seed(c, tx, t, "Rahul Verma", $"rahul{U()}@school.test", null, "ADM-3");
        var parent = await Seed(c, tx, t, null, null, "9111111113", "ADM-3");
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        var sp = await Pid(c, tx, student); var pp = await Pid(c, tx, parent);
        sp.Should().NotBeNull(); pp.Should().NotBeNull();
        pp!.Value.Should().NotBe(sp!.Value);
    }

    [Fact]
    public async Task One_parent_linked_to_multiple_children_keeps_a_single_PersonId()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var mail = $"mum{U()}@home.test";
        // Same parent contact, different child pointers (the tenant email index allows one row per
        // email per tenant, so the second login sits in another tenant): children never split the person.
        var p1 = await Seed(c, tx, Guid.NewGuid(), "Meena K", mail, null, "ADM-4A");
        var p2 = await Seed(c, tx, Guid.NewGuid(), "Meena K", mail, null, "ADM-4B");
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        var a = await Pid(c, tx, p1); var b = await Pid(c, tx, p2);
        a.Should().NotBeNull();
        b.Should().Be(a);
    }

    [Fact]
    public async Task Parent_and_child_sharing_no_contact_info_get_distinct_PersonIds()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var t = Guid.NewGuid();
        var child = await Seed(c, tx, t, "Asha P", $"asha{U()}@school.test", "9222222225", "ADM-5");
        var parent = await Seed(c, tx, t, "Asha P", $"parent{U()}@home.test", "9333333335", "ADM-5");
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        var cp = await Pid(c, tx, child); var pp = await Pid(c, tx, parent);
        cp.Should().NotBeNull(); pp.Should().NotBeNull();
        pp!.Value.Should().NotBe(cp!.Value);
    }

    [Fact]
    public async Task Same_email_and_same_name_across_tenants_share_one_PersonId()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var mail = $"teacher{U()}@x.test";
        var a = await Seed(c, tx, Guid.NewGuid(), "Priya N", mail, null, null);
        var b = await Seed(c, tx, Guid.NewGuid(), "  priya n ", mail.ToUpperInvariant(), null, null);
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        var pa = await Pid(c, tx, a); var pb = await Pid(c, tx, b);
        pa.Should().NotBeNull();
        pb.Should().Be(pa);
    }

    [Fact]
    public async Task Same_email_with_two_different_names_stays_NULL_as_ambiguous()
    {
        var (c, tx) = await OpenAsync();
        await using var _c = c; await using var _t = tx;
        var mail = $"shared{U()}@x.test";
        var a = await Seed(c, tx, Guid.NewGuid(), "Anil One", mail, null, null);
        var b = await Seed(c, tx, Guid.NewGuid(), "Bina Two", mail, null, null);
        await c.ExecuteAsync(BackfillSql(), transaction: tx);
        (await Pid(c, tx, a)).Should().BeNull();
        (await Pid(c, tx, b)).Should().BeNull();
    }
}
