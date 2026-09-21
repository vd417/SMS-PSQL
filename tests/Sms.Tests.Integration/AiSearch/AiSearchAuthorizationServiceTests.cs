using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// The single authorization choke point: every scope value must be re-derived from the
/// authenticated caller, and anything the LLM-extracted filters claimed beyond that scope
/// must be clamped away (never answered, never leaked as "exists but forbidden").
[Collection("sql")]
public class AiSearchAuthorizationServiceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.SchoolAdmin], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private async Task<Guid> ParentUserId(string email, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        return await conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId
              AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@email)))
            """,
            new { email, tenantId });
    }

    private async Task<Guid> StudentUserId(string admissionNo, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        return await conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId
              AND LOWER(LTRIM(RTRIM(StudentId))) = LOWER(LTRIM(RTRIM(@admissionNo)))
            """,
            new { admissionNo, tenantId });
    }

    /// Runs <paramref name="act"/> against a scope whose ambient ITenantContext is the caller,
    /// exactly as the request pipeline would have set it after JWT validation.
    private static async Task<AiAuthorizationResult> AsCaller(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId,
        Func<IAiSearchAuthorizationService, Task<AiAuthorizationResult>> act)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        return await act(scope.ServiceProvider.GetRequiredService<IAiSearchAuthorizationService>());
    }

    [Fact]
    public async Task Parent_querying_an_unlinked_student_name_gets_no_match_not_another_childs_data()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var aisha = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-AI-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        // Unrelated student in the same tenant — the parent must never resolve to this row.
        var rahul = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-RA-{Guid.NewGuid():N}"[..20],
            name = "Rahul Verma",
            grade = "V",
            section = "A",
            roll = 2,
            guardian_email = $"other{Guid.NewGuid():N}@home.test",
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var result = await AsCaller(app, tenantId, parentId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("Rahul", null, null, "today", false),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        result.ResultIntent.Should().Be("StudentAttendance");
        result.ResolvedStudentId.Should().BeNull();
        result.ClampedFilters.StudentName.Should().BeNull();
        result.AllowedChildStudentIds.Should().BeEquivalentTo([aisha.GetProperty("id").GetGuid()]);
        result.AllowedChildStudentIds.Should().NotContain(rahul.GetProperty("id").GetGuid());
        // A name WAS asked and matched nothing the parent may see — distinguishable from "no name asked".
        result.NameUnmatched.Should().BeTrue();
        result.Unrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task Parent_querying_their_own_child_by_name_resolves_that_child()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var aisha = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-AI-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var result = await AsCaller(app, tenantId, parentId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("aisha", null, null, "today", false),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        result.ResolvedStudentId.Should().Be(aisha.GetProperty("id").GetGuid());
        result.ClampedFilters.StudentName.Should().Be("aisha");
        result.NameUnmatched.Should().BeFalse();
        // Even with a non-empty child list, a parent is never unrestricted.
        result.Unrestricted.Should().BeFalse();
    }

    /// Spec invariant: TargetSelf must resolve to the CALLER'S OWN record and ignore any other
    /// name the LLM extracted — "my attendance" can never be redirected at another student.
    [Fact]
    public async Task Student_with_TargetSelf_resolves_to_their_own_record_ignoring_any_other_name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var admissionNo = $"ADM-SELF-{Guid.NewGuid():N}"[..20];

        var me = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Nikhil Rao",
            grade = "VI",
            section = "A",
            roll = 1,
        }), HttpStatusCode.Created);

        // A different student the caller must never be redirected at.
        var someoneElse = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-OTH-{Guid.NewGuid():N}"[..20],
            name = "SomeoneElse Gupta",
            grade = "VI",
            section = "B",
            roll = 2,
        }), HttpStatusCode.Created);

        var studentUserId = await StudentUserId(admissionNo, tenantId);

        var result = await AsCaller(app, tenantId, studentUserId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("SomeoneElse", null, null, "today", TargetSelf: true),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        result.ResolvedStudentId.Should().Be(me.GetProperty("id").GetGuid());
        result.ResolvedStudentId.Should().NotBe(someoneElse.GetProperty("id").GetGuid());
        result.ClampedFilters.StudentName.Should().BeNull();
        result.Unrestricted.Should().BeFalse();
        result.NameUnmatched.Should().BeFalse();
    }

    /// Pins the ACTUAL parent + TargetSelf behaviour, which is not "denied": a guardian's Users row
    /// is provisioned by EnsureParentLoginAsync with StudentId = the linked child's admission no, so
    /// GetMyStudentAsync resolves for a parent too and TargetSelf lands on that linked child. The
    /// security invariant that matters — and is asserted here — is that whatever it resolves to is
    /// inside the caller's authorized set and never the unrelated student whose name was extracted.
    [Fact]
    public async Task Parent_with_TargetSelf_resolves_only_within_their_own_linked_children()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var aisha = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-PS-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var outsider = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-PO-{Guid.NewGuid():N}"[..20],
            name = "SomeoneElse Gupta",
            grade = "IV",
            section = "C",
            roll = 2,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var result = await AsCaller(app, tenantId, parentId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("SomeoneElse", null, null, "today", TargetSelf: true),
            [Policies.StudentOrParent]));

        result.ClampedFilters.StudentName.Should().BeNull();
        result.ResolvedStudentId.Should().NotBe(outsider.GetProperty("id").GetGuid());
        result.Unrestricted.Should().BeFalse();
        if (result.Allowed)
            result.ResolvedStudentId.Should().Be(aisha.GetProperty("id").GetGuid());
        else
            result.ResultIntent.Should().Be("Forbidden");
    }

    /// A same-named student in another tenant must be invisible: name resolution runs only over
    /// the caller's own ParentStudentLinks, and RLS keeps the other tenant's row out entirely.
    [Fact]
    public async Task Parent_asking_by_name_never_resolves_a_same_named_student_in_another_tenant()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = Admin(app, tenantA);
        var adminB = Admin(app, tenantB);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var aishaA = await Data(await adminA.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-TA-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        // Same name, different tenant, unrelated to the tenant-A parent.
        var aishaB = await Data(await adminB.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-TB-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = $"other{Guid.NewGuid():N}@home.test",
        }), HttpStatusCode.Created);

        aishaB.GetProperty("id").GetGuid().Should().NotBe(aishaA.GetProperty("id").GetGuid());

        var parentId = await ParentUserId(parentEmail, tenantA);

        var result = await AsCaller(app, tenantA, parentId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("Aisha", null, null, "today", false),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        result.ResolvedStudentId.Should().Be(aishaA.GetProperty("id").GetGuid());
        result.ResolvedStudentId.Should().NotBe(aishaB.GetProperty("id").GetGuid());
        result.AllowedChildStudentIds.Should().BeEquivalentTo([aishaA.GetProperty("id").GetGuid()]);
        result.AllowedChildStudentIds.Should().NotContain(aishaB.GetProperty("id").GetGuid());
        result.Unrestricted.Should().BeFalse();
        result.NameUnmatched.Should().BeFalse();
    }

    [Fact]
    public async Task Teacher_querying_a_class_they_do_not_teach_has_the_class_filter_clamped_away()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            var teacherId = Guid.NewGuid();
            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId)",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, N'Meena', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId)
                VALUES (@classId, @tenantId, N'8A', 0, @teacherId)
                """,
                new { classId, tenantId, teacherId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId)
                VALUES (@tenantId, 'Mon', 1, N'Math', @classId, N'8A', @teacherId)
                """,
                new { tenantId, classId, teacherId });
        });

        var result = await AsCaller(app, tenantId, teacherUserId, svc => svc.AuthorizeAsync(
            "ClassAttendance",
            new AiSearchFilters(null, "9B", "B", "today", false),
            [Policies.Teacher]));

        result.Allowed.Should().BeTrue();
        result.AllowedClassNames.Should().BeEquivalentTo(["8A"]);
        result.ClampedFilters.ClassName.Should().BeNull();
        result.ClampedFilters.Section.Should().BeNull();
        result.Unrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task Teacher_querying_a_class_they_do_teach_keeps_the_class_filter()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            var teacherId = Guid.NewGuid();
            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId)",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, N'Meena', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId)
                VALUES (@classId, @tenantId, N'8A', 0, @teacherId)
                """,
                new { classId, tenantId, teacherId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId)
                VALUES (@tenantId, 'Mon', 1, N'Math', @classId, N'8A', @teacherId)
                """,
                new { tenantId, classId, teacherId });
        });

        var result = await AsCaller(app, tenantId, teacherUserId, svc => svc.AuthorizeAsync(
            "ClassAttendance",
            new AiSearchFilters(null, "8a", "A", "today", false),
            [Policies.Teacher]));

        result.Allowed.Should().BeTrue();
        result.ClampedFilters.ClassName.Should().Be("8a");
        result.ClampedFilters.Section.Should().Be("A");
        // Teaching the asked-about class does not promote a teacher to whole-tenant scope.
        result.Unrestricted.Should().BeFalse();
        result.AllowedClassNames.Should().BeEquivalentTo(["8A"]);
    }

    [Fact]
    public async Task Staff_role_is_denied_for_DashboardSummary()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var result = await AsCaller(app, tenantId, Guid.NewGuid(), svc => svc.AuthorizeAsync(
            "DashboardSummary",
            new AiSearchFilters(null, null, null, "today", false),
            [Policies.Staff]));

        result.Allowed.Should().BeFalse();
        result.ResultIntent.Should().Be("Forbidden");
        result.ResolvedStudentId.Should().BeNull();
        result.AllowedChildStudentIds.Should().BeNull();
        result.AllowedClassNames.Should().BeNull();
        // Denied must never look like "unrestricted" to a handler.
        result.Unrestricted.Should().BeFalse();
        result.NameUnmatched.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_intent_is_denied()
    {
        await using var app = App();

        var result = await AsCaller(app, Guid.NewGuid(), Guid.NewGuid(), svc => svc.AuthorizeAsync(
            "DropAllTables",
            new AiSearchFilters(null, null, null, null, false),
            [Policies.SchoolAdmin]));

        result.Allowed.Should().BeFalse();
        result.ResultIntent.Should().Be("Forbidden");
        result.Unrestricted.Should().BeFalse();
    }

    /// Finding 2: Section must be independently validated, not merely as a side effect of the
    /// ClassName check. This teacher teaches ONLY grade 8 section A (a real Classes row with Grade
    /// and Section populated, plus a matching TimetableSlots row) — a same-grade section B class also
    /// exists in the tenant but is taught by someone else. Asking about section B (with no ClassName
    /// filter to trigger the old side-effect-only check) must still be clamped away.
    [Fact]
    public async Task Teacher_asking_about_a_section_they_do_not_teach_is_clamped_even_without_a_class_name_filter()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var otherTeacherUserId = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });

            var teacherId = Guid.NewGuid();
            var otherTeacherId = Guid.NewGuid();
            var classAId = Guid.NewGuid();
            var classBId = Guid.NewGuid();

            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId), (@otherTeacherUserId, @tenantId)",
                new { teacherUserId, otherTeacherUserId, tenantId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES
                (@teacherId, @tenantId, N'Meena', @teacherUserId),
                (@otherTeacherId, @tenantId, N'Asha', @otherTeacherUserId)
                """,
                new { teacherId, otherTeacherId, tenantId, teacherUserId, otherTeacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, StudentCount, ClassTeacherId) VALUES
                (@classAId, @tenantId, N'8-A', N'8', N'A', 0, @teacherId),
                (@classBId, @tenantId, N'8-B', N'8', N'B', 0, @otherTeacherId)
                """,
                new { classAId, classBId, tenantId, teacherId, otherTeacherId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId) VALUES
                (@tenantId, 'Mon', 1, N'Math', @classAId, N'8-A', @teacherId),
                (@tenantId, 'Mon', 2, N'Math', @classBId, N'8-B', @otherTeacherId)
                """,
                new { tenantId, classAId, classBId, teacherId, otherTeacherId });
        });

        // No ClassName asked — only Section "B", which this teacher does not teach at all.
        var result = await AsCaller(app, tenantId, teacherUserId, svc => svc.AuthorizeAsync(
            "ClassAttendance",
            new AiSearchFilters(null, null, "B", "today", false),
            [Policies.Teacher]));

        result.Allowed.Should().BeTrue();
        result.ClampedFilters.Section.Should().BeNull(
            "a teacher must never be able to reach a section they don't teach just by omitting ClassName");
        result.ClampedFilters.ClassName.Should().BeNull();
        result.Unrestricted.Should().BeFalse();
    }

    /// Finding 2, ClassName-present variant: the teacher teaches class "8-A" and asks about it by
    /// name, but pairs it with a Section that belongs to a DIFFERENT class ("8-B") they don't teach —
    /// this must be clamped even though the ClassName itself is one they are authorized for.
    [Fact]
    public async Task Teacher_pairing_their_own_class_name_with_someone_elses_section_is_clamped()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var otherTeacherUserId = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });

            var teacherId = Guid.NewGuid();
            var otherTeacherId = Guid.NewGuid();
            var classAId = Guid.NewGuid();
            var classBId = Guid.NewGuid();

            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId), (@otherTeacherUserId, @tenantId)",
                new { teacherUserId, otherTeacherUserId, tenantId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES
                (@teacherId, @tenantId, N'Meena', @teacherUserId),
                (@otherTeacherId, @tenantId, N'Asha', @otherTeacherUserId)
                """,
                new { teacherId, otherTeacherId, tenantId, teacherUserId, otherTeacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, StudentCount, ClassTeacherId) VALUES
                (@classAId, @tenantId, N'8-A', N'8', N'A', 0, @teacherId),
                (@classBId, @tenantId, N'8-B', N'8', N'B', 0, @otherTeacherId)
                """,
                new { classAId, classBId, tenantId, teacherId, otherTeacherId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId) VALUES
                (@tenantId, 'Mon', 1, N'Math', @classAId, N'8-A', @teacherId),
                (@tenantId, 'Mon', 2, N'Math', @classBId, N'8-B', @otherTeacherId)
                """,
                new { tenantId, classAId, classBId, teacherId, otherTeacherId });
        });

        var result = await AsCaller(app, tenantId, teacherUserId, svc => svc.AuthorizeAsync(
            "ClassAttendance",
            new AiSearchFilters(null, "8-A", "B", "today", false),
            [Policies.Teacher]));

        result.Allowed.Should().BeTrue();
        result.ClampedFilters.ClassName.Should().BeNull(
            "the class name they teach must not smuggle through a section they don't teach");
        result.ClampedFilters.Section.Should().BeNull();
        result.Unrestricted.Should().BeFalse();
    }

    /// GreetById reuses the StudentName filter field to carry a scanned admission number/employee
    /// code, not a person's name — the generic name-matching narrowing (which would null it out
    /// because an ID practically never Contains-matches a child's name) must be bypassed entirely
    /// for this one intent, while still handing back the caller's real scope.
    [Fact]
    public async Task GreetById_for_a_parent_passes_the_raw_scanned_code_through_unclamped()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var aisha = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-GB-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var result = await AsCaller(app, tenantId, parentId, svc => svc.AuthorizeAsync(
            "GreetById",
            new AiSearchFilters("SCANNED-CODE-123", null, null, null, false),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        // The raw scanned code must survive completely unchanged — never nulled by name-matching.
        result.ClampedFilters.StudentName.Should().Be("SCANNED-CODE-123");
        result.AllowedChildStudentIds.Should().BeEquivalentTo([aisha.GetProperty("id").GetGuid()]);
        result.Unrestricted.Should().BeFalse();
        result.NameUnmatched.Should().BeFalse();
    }

    [Fact]
    public async Task GreetById_for_a_teacher_passes_ClassName_and_Section_through_unclamped()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            var teacherId = Guid.NewGuid();
            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId)",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, N'Meena', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId)
                VALUES (@classId, @tenantId, N'8A', 0, @teacherId)
                """,
                new { classId, tenantId, teacherId });
        });

        var result = await AsCaller(app, tenantId, teacherUserId, svc => svc.AuthorizeAsync(
            "GreetById",
            new AiSearchFilters("SCANNED-4521", "9B", "B", null, false),
            [Policies.Teacher]));

        result.Allowed.Should().BeTrue();
        // GreetById never asks the generic ClassName/Section narrowing to run for this intent —
        // whatever was extracted (even a class the teacher doesn't teach) must pass through untouched.
        result.ClampedFilters.StudentName.Should().Be("SCANNED-4521");
        result.ClampedFilters.ClassName.Should().Be("9B");
        result.ClampedFilters.Section.Should().Be("B");
        result.AllowedClassNames.Should().BeEquivalentTo(["8A"]);
        result.Unrestricted.Should().BeFalse();
    }

    /// Regression guard: a NON-GreetById intent for the same parent must keep the existing
    /// name-matching/nulling behaviour completely unchanged by this new intent-gated branch.
    [Fact]
    public async Task Non_GreetById_intent_for_a_parent_still_gets_name_matching_and_nulling()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-GB2-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var result = await AsCaller(app, tenantId, parentId, svc => svc.AuthorizeAsync(
            "StudentAttendance",
            new AiSearchFilters("SCANNED-CODE-123", null, null, "today", false),
            [Policies.StudentOrParent]));

        result.Allowed.Should().BeTrue();
        // An ID-shaped string does not Contains-match "Aisha Khan" — old behaviour nulls it out.
        result.ClampedFilters.StudentName.Should().BeNull();
        result.NameUnmatched.Should().BeTrue();
        result.Unrestricted.Should().BeFalse();
    }

    [Fact]
    public async Task GreetById_for_admin_like_caller_is_unrestricted_with_filters_unclamped()
    {
        await using var app = App();

        var result = await AsCaller(app, Guid.NewGuid(), Guid.NewGuid(), svc => svc.AuthorizeAsync(
            "GreetById",
            new AiSearchFilters("SCANNED-9999", null, null, null, false),
            [Policies.SchoolAdmin]));

        result.Allowed.Should().BeTrue();
        result.ClampedFilters.StudentName.Should().Be("SCANNED-9999");
        result.Unrestricted.Should().BeTrue();
    }

    [Fact]
    public async Task Driver_role_is_not_Unrestricted_it_is_self_scoped_like_TargetSelf()
    {
        await using var app = App();

        var result = await AsCaller(app, Guid.NewGuid(), Guid.NewGuid(), svc => svc.AuthorizeAsync(
            "MyTripStatus",
            new AiSearchFilters(null, null, null, null, false),
            ["driver"]));

        result.Allowed.Should().BeTrue();
        result.Unrestricted.Should().BeFalse(
            "a driver's own-trip lookup is self-scoped, never whole-tenant, even though no per-record clamp list applies");
        result.AllowedChildStudentIds.Should().BeNull();
        result.AllowedClassNames.Should().BeNull();
    }

    [Fact]
    public async Task Admin_filters_pass_through_unclamped()
    {
        await using var app = App();

        var result = await AsCaller(app, Guid.NewGuid(), Guid.NewGuid(), svc => svc.AuthorizeAsync(
            "ClassAttendance",
            new AiSearchFilters(null, "9B", "B", "today", false),
            [Policies.SchoolAdmin]));

        result.Allowed.Should().BeTrue();
        result.ClampedFilters.ClassName.Should().Be("9B");
        result.ClampedFilters.Section.Should().Be("B");
        result.AllowedClassNames.Should().BeNull();
        // The only path where null clamp lists legitimately mean "no filter".
        result.Unrestricted.Should().BeTrue();
    }
}
