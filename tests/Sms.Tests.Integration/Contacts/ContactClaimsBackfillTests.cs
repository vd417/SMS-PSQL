using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

// B7: dbo.contact_claims_backfill() populates dbo."ContactClaims" from the contacts of EXISTING
// student/teacher/staff profiles — the pre-feature rows that were created before enforcement
// existed and therefore hold no claim yet. It does this by replaying the authoritative
// dbo.contact_claims_sync for each owner (PersonId NULL, exactly the arg shape the create/update
// procs use), so the backfilled claims are byte-identical to what runtime would have produced and
// a later legitimate profile update self-matches its own claim by (OwnerType, OwnerId).
//
// Only own contacts are claimed: a student's OWN Email (students have no own-phone column), and a
// teacher's / staff's Email + Phone. Guardian email/phone are denormalized guardian fields and are
// NEVER claimed. The function is idempotent (re-running syncs each owner's own claim = no-op) and
// self-guarding: two DIFFERENT profiles sharing a normalized contact in one tenant raise the SMSDC
// sentinel, which aborts the 0014 migration — the gated STOP against pre-existing conflicts, since
// the unique index (0012) already exists before this backfill runs.
[Collection("sql")]
public class ContactClaimsBackfillTests(PostgresFixture fx)
{
    // Opens a platform-scoped connection inside a transaction that is never committed, so each test
    // is fully isolated (profiles + claims vanish on dispose).
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

    private static async Task ExecAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql,
        params NpgsqlParameter[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddRange(ps);
        await cmd.ExecuteNonQueryAsync();
    }

    // Raw INSERTs that deliberately BYPASS the create procs, so no claim is synced — these stand in
    // for pre-feature rows the backfill must register.
    private static Task InsertStudentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant,
        string admissionNo, string? email, string? guardianEmail = null, string? guardianPhone = null) =>
        ExecAsync(c, tx,
            """
            INSERT INTO dbo."Students" ("TenantId","AdmissionNo","Name","Email","GuardianEmail","GuardianPhone")
            VALUES ($1,$2,$3,$4,$5,$6)
            """,
            P(NpgsqlDbType.Uuid, tenant), P(NpgsqlDbType.Varchar, admissionNo),
            P(NpgsqlDbType.Varchar, "Student " + admissionNo), P(NpgsqlDbType.Varchar, email),
            P(NpgsqlDbType.Varchar, guardianEmail), P(NpgsqlDbType.Varchar, guardianPhone));

    private static Task InsertTeacherAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant,
        string? email, string? phone, string name = "Teacher") =>
        ExecAsync(c, tx,
            """INSERT INTO dbo."Teachers" ("TenantId","Name","Email","Phone") VALUES ($1,$2,$3,$4)""",
            P(NpgsqlDbType.Uuid, tenant), P(NpgsqlDbType.Varchar, name),
            P(NpgsqlDbType.Varchar, email), P(NpgsqlDbType.Varchar, phone));

    private static Task InsertStaffAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant,
        string? email, string? phone, string name = "Staff") =>
        ExecAsync(c, tx,
            """INSERT INTO dbo."Staff" ("TenantId","Name","Email","Phone") VALUES ($1,$2,$3,$4)""",
            P(NpgsqlDbType.Uuid, tenant), P(NpgsqlDbType.Varchar, name),
            P(NpgsqlDbType.Varchar, email), P(NpgsqlDbType.Varchar, phone));

    private static async Task<Guid> InsertTeacherReturningIdAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, string? email, string? phone, string name = "Teacher")
    {
        await using var cmd = new NpgsqlCommand(
            """INSERT INTO dbo."Teachers" ("TenantId","Name","Email","Phone") VALUES ($1,$2,$3,$4) RETURNING "Id" """,
            c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Varchar, name));
        cmd.Parameters.Add(P(NpgsqlDbType.Varchar, email));
        cmd.Parameters.Add(P(NpgsqlDbType.Varchar, phone));
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> OwnerIdOfAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, string kind, string value)
    {
        await using var cmd = new NpgsqlCommand(
            """SELECT "OwnerId" FROM dbo."ContactClaims" WHERE "TenantId"=$1 AND "Kind"=$2 AND "NormalizedValue"=$3""",
            c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, kind));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, value));
        return (string?)await cmd.ExecuteScalarAsync();
    }

    // Replays exactly what a *_update proc does at the end of its transaction: PERFORM
    // contact_claims_sync for the same profile owner with PersonId NULL. Used to prove a later
    // legitimate update recognises its own backfilled claim instead of raising a bogus conflict.
    private static async Task SyncOwnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant,
        string ownerType, string ownerId, string? email, string? phone)
    {
        await using var cmd = new NpgsqlCommand("SELECT dbo.contact_claims_sync($1,$2,$3,$4,$5,$6)", c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, ownerType));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, ownerId));
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, null));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, email));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, phone));
        await cmd.ExecuteNonQueryAsync();
    }

    // Runs the backfill inside a savepoint so a raised conflict does not poison the test transaction.
    private static async Task BackfillAsync(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        await using (var sp = new NpgsqlCommand("SAVEPOINT b", c, tx)) await sp.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT dbo.contact_claims_backfill()", c, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT b", c, tx);
            await rb.ExecuteNonQueryAsync();
            throw;
        }
    }

    private static async Task<long> CountClaimsAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant,
        string? kind = null, string? value = null)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM dbo."ContactClaims"
             WHERE "TenantId" = $1
               AND ($2::text IS NULL OR "Kind" = $2)
               AND ($3::text IS NULL OR "NormalizedValue" = $3)
            """, c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, kind));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, value));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> OwnerTypeOfAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        Guid tenant, string kind, string value)
    {
        await using var cmd = new NpgsqlCommand(
            """SELECT "OwnerType" FROM dbo."ContactClaims" WHERE "TenantId"=$1 AND "Kind"=$2 AND "NormalizedValue"=$3""",
            c, tx);
        cmd.Parameters.Add(P(NpgsqlDbType.Uuid, tenant));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, kind));
        cmd.Parameters.Add(P(NpgsqlDbType.Text, value));
        return (string?)await cmd.ExecuteScalarAsync();
    }

    private static async Task ShouldConflictAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("SMSDC");

    // Each existing student's OWN email becomes exactly one email claim; no phone claim (no own-phone).
    [Fact]
    public async Task Backfills_one_email_claim_per_student_and_no_phone()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertStudentAsync(c, tx, t, "A1", "stud1@x.com");
        await InsertStudentAsync(c, tx, t, "A2", "stud2@x.com");
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t, "email")).Should().Be(2);
        (await CountClaimsAsync(c, tx, t, "phone")).Should().Be(0);
        (await OwnerTypeOfAsync(c, tx, t, "email", "stud1@x.com")).Should().Be("student");
    }

    // Teacher and staff contribute both their email AND phone claims.
    [Fact]
    public async Task Backfills_email_and_phone_for_teacher_and_staff()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t, "teach@x.com", "9811100001");
        await InsertStaffAsync(c, tx, t, "staff@x.com", "9811100002");
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t, "email")).Should().Be(2);
        (await CountClaimsAsync(c, tx, t, "phone")).Should().Be(2);
        (await OwnerTypeOfAsync(c, tx, t, "email", "teach@x.com")).Should().Be("teacher");
        (await OwnerTypeOfAsync(c, tx, t, "phone", "9811100002")).Should().Be("staff");
    }

    // Re-running the backfill never duplicates or raises for already-claimed owners.
    [Fact]
    public async Task Is_idempotent()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t, "idem@x.com", "9822200001");
        await BackfillAsync(c, tx);
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t)).Should().Be(2); // one email + one phone, not doubled
    }

    // Two DIFFERENT profiles sharing a normalized email in one tenant is a genuine pre-existing
    // conflict -> the backfill raises SMSDC (which, as a migration, is the gated STOP).
    [Fact]
    public async Task Raises_when_two_profiles_share_an_email_in_one_tenant()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t, "Clash@X.com", null);
        await InsertStaffAsync(c, tx, t, "clash@x.com", null); // same normalized email, different owner

        await ShouldConflictAsync(() => BackfillAsync(c, tx));
    }

    // Phone normalization applies during backfill: +91-prefixed and bare forms of the same number
    // on two different owners collide (Review Focus #1).
    [Fact]
    public async Task Raises_when_plus91_and_bare_phone_collide_across_owners()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t, "ph1@x.com", "+91 98765 43210");
        await InsertStaffAsync(c, tx, t, "ph2@x.com", "9876543210");

        await ShouldConflictAsync(() => BackfillAsync(c, tx));
    }

    // The same contact value in DIFFERENT tenants is not a conflict: one claim per tenant.
    [Fact]
    public async Task Same_value_across_tenants_is_allowed()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t1, "shared@x.com", null);
        await InsertStaffAsync(c, tx, t2, "shared@x.com", null);
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t1, "email", "shared@x.com")).Should().Be(1);
        (await CountClaimsAsync(c, tx, t2, "email", "shared@x.com")).Should().Be(1);
    }

    // Guardian email/phone are denormalized and must never be claimed; a student with only guardian
    // contacts (no own email) yields no claim, and two siblings may share a guardian email freely.
    [Fact]
    public async Task Never_claims_guardian_fields()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertStudentAsync(c, tx, t, "G1", email: null,
            guardianEmail: "parent@x.com", guardianPhone: "9833300001");
        await InsertStudentAsync(c, tx, t, "G2", email: null,
            guardianEmail: "parent@x.com", guardianPhone: "9833300001"); // same guardian, no own email
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t)).Should().Be(0);
    }

    // A student with BOTH own email and guardian contacts yields exactly one claim: the own email.
    [Fact]
    public async Task Claims_own_email_but_not_the_guardian_email_on_the_same_student()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertStudentAsync(c, tx, t, "G3", email: "own@x.com",
            guardianEmail: "guard@x.com", guardianPhone: "9844400001");
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t)).Should().Be(1);
        (await CountClaimsAsync(c, tx, t, "email", "own@x.com")).Should().Be(1);
        (await CountClaimsAsync(c, tx, t, "email", "guard@x.com")).Should().Be(0);
    }

    // Null / empty contacts reserve no slot and never collide with one another.
    [Fact]
    public async Task Skips_null_and_empty_contacts()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertStudentAsync(c, tx, t, "N1", email: null);
        await InsertTeacherAsync(c, tx, t, "", "");   // empty strings normalize to NULL
        await InsertStaffAsync(c, tx, t, "", "");
        await BackfillAsync(c, tx);

        (await CountClaimsAsync(c, tx, t)).Should().Be(0);
    }

    // The core guarantee of the "profiles only, owner identity preserved" design: the backfilled
    // claim is stored under the profile's OWN Id, so a later legitimate update of that same profile
    // (which PERFORMs contact_claims_sync with the same owner + PersonId NULL) self-matches its own
    // claim and does NOT raise a conflict. Directly exercises the property the design hinges on,
    // rather than re-running the backfill with identical args.
    [Fact]
    public async Task Backfilled_claim_is_owned_by_the_profile_and_a_later_update_reclaims_it_without_conflict()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        var teacherId = await InsertTeacherReturningIdAsync(c, tx, t, "self@x.com", "9855500001");
        await BackfillAsync(c, tx);

        // The claim is owned by this exact profile Id.
        (await OwnerIdOfAsync(c, tx, t, "email", "self@x.com")).Should().Be(teacherId.ToString());

        // A later update of the same teacher (name change -> email/phone unchanged) re-syncs the same
        // owner and must NOT raise, and must not create a second claim.
        await SyncOwnerAsync(c, tx, t, "teacher", teacherId.ToString(), "self@x.com", "9855500001");
        (await CountClaimsAsync(c, tx, t, "email", "self@x.com")).Should().Be(1);
        (await CountClaimsAsync(c, tx, t, "phone", "9855500001")).Should().Be(1);
    }

    // Same-owner-type collision (two teachers sharing an email in one tenant) is also a genuine
    // conflict -> SMSDC. Complements the cross-type (teacher vs staff) collision test above.
    [Fact]
    public async Task Raises_when_two_teachers_share_an_email_in_one_tenant()
    {
        var t = Guid.NewGuid();
        var (c, tx) = await BeginAsync();
        await using var _c = c; await using var _t = tx;

        await InsertTeacherAsync(c, tx, t, "dup.teacher@x.com", null, name: "Teacher One");
        await InsertTeacherAsync(c, tx, t, "dup.teacher@x.com", null, name: "Teacher Two");

        await ShouldConflictAsync(() => BackfillAsync(c, tx));
    }
}
