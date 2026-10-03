using Dapper;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Comms;

/// Covers dbo.thread_create's conversation-identity adoption (migrations 0017/0018): a reply mirror
/// must continue the SAME conversation instead of spawning a duplicate thread when the sender's
/// current display name differs from the name stored on the recipient's legacy thread
/// (e.g. "Neha Singh" vs "Principal"). Identity is resolved via dbo.resolve_thread_contact, which
/// only resolves unambiguously — two different people sharing a name are never merged.
[Collection("sql")]
public class ThreadAdoptionTests(PostgresFixture fx)
{
    private async Task<NpgsqlConnection> OpenAsync(Guid tenant)
    {
        var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenant });
        return c;
    }

    private static Task<Guid> ThreadCreateAsync(
        NpgsqlConnection c, Guid tenant, Guid owner, string name, string? role, Guid? contactUserId,
        bool group = false, Guid? childId = null) =>
        c.QuerySingleAsync<Guid>(
            """SELECT "Id" FROM dbo.thread_create(@t::uuid,@o::uuid,@n::varchar,@r::varchar,@g::boolean,@c::uuid,@cu::uuid)""",
            new { t = tenant, o = owner, n = name, r = role, g = group, c = childId, cu = contactUserId });

    private static Task InsertUserAsync(NpgsqlConnection c, Guid id, Guid tenant, string? name = null) =>
        c.ExecuteAsync(
            """INSERT INTO "dbo"."Users" ("Id","TenantId","Name") VALUES (@id,@t,@n)""",
            new { id, t = tenant, n = name });

    private static Task InsertTeacherAsync(NpgsqlConnection c, Guid id, Guid tenant, string name, Guid? userId) =>
        c.ExecuteAsync(
            """INSERT INTO "dbo"."Teachers" ("Id","TenantId","Name","UserId") VALUES (@id,@t,@n,@u)""",
            new { id, t = tenant, n = name, u = userId });

    private static Task InsertStaffAsync(NpgsqlConnection c, Guid id, Guid tenant, string name, Guid? userId) =>
        c.ExecuteAsync(
            """INSERT INTO "dbo"."Staff" ("Id","TenantId","Name","UserId") VALUES (@id,@t,@n,@u)""",
            new { id, t = tenant, n = name, u = userId });

    private static async Task LinkStudentParentAsync(
        NpgsqlConnection c, Guid tenant, Guid studentId, string studentName, Guid parentUserId)
    {
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Students" ("Id","TenantId","AdmissionNo","Name") VALUES (@id,@t,@adm,@n)""",
            new { id = studentId, t = tenant, adm = "A-" + studentId.ToString("N")[..6], n = studentName });
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."ParentStudentLinks" ("ParentUserId","StudentId","TenantId") VALUES (@p,@s,@t)""",
            new { p = parentUserId, s = studentId, t = tenant });
    }

    private static Task<Guid> InsertLegacyThreadAsync(
        NpgsqlConnection c, Guid tenant, Guid owner, string name, string? role) =>
        c.QuerySingleAsync<Guid>(
            """
            INSERT INTO "dbo"."ChatThreads" ("Id","TenantId","OwnerUserId","Name","Role","ContactUserId")
            VALUES (gen_random_uuid(),@t,@o,@n,@r,NULL) RETURNING "Id"
            """,
            new { t = tenant, o = owner, n = name, r = role });

    private static Task<Guid?> ContactUserIdOfAsync(NpgsqlConnection c, Guid threadId) =>
        c.QuerySingleAsync<Guid?>("""SELECT "ContactUserId" FROM "dbo"."ChatThreads" WHERE "Id"=@id""", new { id = threadId });

    private static Task<int> ThreadCountAsync(NpgsqlConnection c, Guid tenant, Guid owner, Guid contact) =>
        c.QuerySingleAsync<int>(
            """
            SELECT count(*)::int FROM "dbo"."ChatThreads"
            WHERE "TenantId"=@t AND "OwnerUserId"=@o AND "ContactUserId"=@c
            """,
            new { t = tenant, o = owner, c = contact });

    // 1. An exact ContactUserId match reuses the existing thread (never a second row).
    [Fact]
    public async Task Existing_ContactUserId_is_reused()
    {
        var tenant = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var contact = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, owner, tenant);
        await InsertUserAsync(c, contact, tenant, "Contact Person");

        var first = await ThreadCreateAsync(c, tenant, owner, "Contact Person", "Teacher", contact);
        var again = await ThreadCreateAsync(c, tenant, owner, "A Totally Different Name", "Parent", contact);

        again.Should().Be(first);
        (await ThreadCountAsync(c, tenant, owner, contact)).Should().Be(1);
    }

    // 2. A legacy NULL-ContactUserId thread is adopted by IDENTITY even though the incoming display
    //    name differs ("Neha Singh" stored, mirror arrives as "Principal"). THE REPORTED BUG.
    [Fact]
    public async Task Legacy_null_thread_is_adopted_by_identity_when_display_name_differs()
    {
        var tenant = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var teacherUser = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, parent, tenant, "Rajan");
        await InsertUserAsync(c, teacherUser, tenant);               // Users.Name NULL (like prod)
        await InsertTeacherAsync(c, Guid.NewGuid(), tenant, "Neha Singh", teacherUser);
        var legacy = await InsertLegacyThreadAsync(c, tenant, parent, "Neha Singh", "Arts");

        // Reply mirror: owner=parent, name=sender display "Principal", contact=teacher's user id.
        var adopted = await ThreadCreateAsync(c, tenant, parent, "Principal", "Teacher", teacherUser);

        adopted.Should().Be(legacy, "the differing display name must not spawn a duplicate thread");
        (await ContactUserIdOfAsync(c, adopted)).Should().Be(teacherUser);
        (await ThreadCountAsync(c, tenant, parent, teacherUser)).Should().Be(1);
    }

    // 3. Same user, several different display names across sends -> one thread throughout.
    [Fact]
    public async Task Same_user_different_display_names_stays_one_thread()
    {
        var tenant = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var teacherUser = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, parent, tenant, "Rajan");
        await InsertUserAsync(c, teacherUser, tenant);
        await InsertTeacherAsync(c, Guid.NewGuid(), tenant, "Neha Singh", teacherUser);
        var legacy = await InsertLegacyThreadAsync(c, tenant, parent, "Neha Singh", "Arts");

        var a = await ThreadCreateAsync(c, tenant, parent, "Principal", "Teacher", teacherUser);     // adopt
        var b = await ThreadCreateAsync(c, tenant, parent, "Class Teacher", "Teacher", teacherUser); // reuse
        var d = await ThreadCreateAsync(c, tenant, parent, "Neha", "Teacher", teacherUser);          // reuse

        a.Should().Be(legacy);
        b.Should().Be(legacy);
        d.Should().Be(legacy);
        (await ThreadCountAsync(c, tenant, parent, teacherUser)).Should().Be(1);
    }

    // 4. Two different people who share a name must NEVER be merged (ambiguous identity -> no adopt).
    [Fact]
    public async Task Ambiguous_name_never_merges_different_users()
    {
        var tenant = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var teacherUser1 = Guid.NewGuid();
        var teacherUser2 = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, parent, tenant, "Rajan");
        await InsertUserAsync(c, teacherUser1, tenant);
        await InsertUserAsync(c, teacherUser2, tenant);
        await InsertTeacherAsync(c, Guid.NewGuid(), tenant, "Neha Singh", teacherUser1);
        await InsertTeacherAsync(c, Guid.NewGuid(), tenant, "Neha Singh", teacherUser2); // same name, 2 people
        var legacy = await InsertLegacyThreadAsync(c, tenant, parent, "Neha Singh", "Arts");

        var created = await ThreadCreateAsync(c, tenant, parent, "Principal", "Teacher", teacherUser1);

        created.Should().NotBe(legacy, "ambiguous name resolution must not adopt the wrong person's thread");
        (await ContactUserIdOfAsync(c, legacy)).Should().BeNull("the legacy thread must be left untouched");
        (await ContactUserIdOfAsync(c, created)).Should().Be(teacherUser1);
    }

    // 5 & 6. Teacher -> parent then parent -> teacher resolve to the single shared conversation on
    //        each side (mirror adoption in both directions, no duplicate).
    [Fact]
    public async Task Teacher_parent_roundtrip_uses_one_thread_each_side()
    {
        var tenant = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var teacherUser = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, parent, tenant, "Rajan");
        await InsertUserAsync(c, teacherUser, tenant);
        await InsertTeacherAsync(c, Guid.NewGuid(), tenant, "Neha Singh", teacherUser);

        // Parent started the chat: legacy parent-side thread to "Neha Singh", ContactUserId NULL.
        var parentThread = await InsertLegacyThreadAsync(c, tenant, parent, "Neha Singh", "Arts");
        // Teacher started one too, titled by the parent, ContactUserId NULL.
        var teacherThread = await InsertLegacyThreadAsync(c, tenant, teacherUser, "Rajan", "Parent");

        // Teacher -> parent mirror (owner=parent, display "Principal") adopts the parent's thread.
        var toParent = await ThreadCreateAsync(c, tenant, parent, "Principal", "Teacher", teacherUser);
        // Parent -> teacher mirror (owner=teacher, display "Rajan") adopts the teacher's thread.
        var toTeacher = await ThreadCreateAsync(c, tenant, teacherUser, "Rajan", "Parent", parent);

        toParent.Should().Be(parentThread);
        toTeacher.Should().Be(teacherThread);
        (await ThreadCountAsync(c, tenant, parent, teacherUser)).Should().Be(1);
        (await ThreadCountAsync(c, tenant, teacherUser, parent)).Should().Be(1);
    }

    // 7. Student -> parent legacy flow: a Role='Student' bare-name thread still adopts/reuses via
    //    the parent identity (regression on the earlier student/parent delivery fix).
    [Fact]
    public async Task Student_role_thread_adopts_via_parent_identity()
    {
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var parent = Guid.NewGuid();
        await using var c = await OpenAsync(tenant);
        await InsertUserAsync(c, staff, tenant, "Front Office");
        await InsertUserAsync(c, parent, tenant, "Parent Of Arav");
        await LinkStudentParentAsync(c, tenant, Guid.NewGuid(), "Arav Sharma", parent);

        // Legacy staff-side thread titled by the student, Role Student, ContactUserId NULL.
        var legacy = await InsertLegacyThreadAsync(c, tenant, staff, "Arav Sharma", "Student");
        // A send that resolves the contact to the student's parent must adopt that same thread.
        var adopted = await ThreadCreateAsync(c, tenant, staff, "Arav Sharma", "Student", parent);

        adopted.Should().Be(legacy);
        (await ContactUserIdOfAsync(c, adopted)).Should().Be(parent);
    }

    // 8. Concurrent sends to the same (tenant, owner, contact) create exactly one thread.
    [Fact]
    public async Task Concurrent_creates_do_not_duplicate()
    {
        var tenant = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var contact = Guid.NewGuid();
        await using (var seed = await OpenAsync(tenant))
        {
            await InsertUserAsync(seed, owner, tenant);
            await InsertUserAsync(seed, contact, tenant, "Contact Person");
        }

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var c = await OpenAsync(tenant);
            return await ThreadCreateAsync(c, tenant, owner, "Contact Person", "Teacher", contact);
        });
        var ids = await Task.WhenAll(tasks);

        ids.Distinct().Should().HaveCount(1, "every concurrent create must converge on one thread");
        await using var check = await OpenAsync(tenant);
        (await ThreadCountAsync(check, tenant, owner, contact)).Should().Be(1);
    }

    // 9. Tenant isolation: a same-named legacy thread in another tenant is never adopted.
    [Fact]
    public async Task Adoption_never_crosses_tenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var teacherUserB = Guid.NewGuid();

        Guid legacyA;
        await using (var a = await OpenAsync(tenantA))
        {
            await InsertUserAsync(a, ownerA, tenantA, "Rajan");
            legacyA = await InsertLegacyThreadAsync(a, tenantA, ownerA, "Neha Singh", "Arts");
        }

        await using var b = await OpenAsync(tenantB);
        await InsertUserAsync(b, ownerB, tenantB, "Rajan");
        await InsertUserAsync(b, teacherUserB, tenantB);
        await InsertTeacherAsync(b, Guid.NewGuid(), tenantB, "Neha Singh", teacherUserB);
        var created = await ThreadCreateAsync(b, tenantB, ownerB, "Principal", "Teacher", teacherUserB);

        created.Should().NotBe(legacyA);
        await using var a2 = await OpenAsync(tenantA);
        (await ContactUserIdOfAsync(a2, legacyA)).Should().BeNull("another tenant's thread must stay untouched");
    }
}
