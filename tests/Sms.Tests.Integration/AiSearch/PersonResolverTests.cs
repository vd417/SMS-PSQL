using System.Net;
using System.Net.Http.Json;
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

/// Exercises PersonResolver through the REAL AiSearchAuthorizationService.AuthorizeAsync pipeline
/// (not hand-crafted AiAuthorizationResults) wherever a role's scope-resolution matters -- proving
/// the full authorization-service -> resolver chain never leaks a name the caller is not authorized
/// to see, even when the name genuinely exists elsewhere in the tenant (or in another tenant
/// entirely). Mirrors the seeding/DI conventions established in GreetByIdHandlerTests.cs.
[Collection("sql")]
public class PersonResolverTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string Intent = "PersonLookup";

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

    private static async Task<System.Text.Json.JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await work(conn);
    }

    private async Task SeedInTenant(Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await work(conn);
        });
    }

    private async Task<Guid> ParentUserId(string email, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        return await conn.QuerySingleAsync<Guid>(
            """
            SELECT "Id" FROM "dbo"."Users"
            WHERE "TenantId" = @tenantId
              AND lower(trim("Email")) = lower(trim(@email))
            """,
            new { email, tenantId });
    }

    /// Seeds a teacher whose taught class has a free-text Name that does NOT already look like a
    /// compacted "Grade-Section" label (e.g. "Section Eight A"), alongside a real Grade/Section on
    /// the class row itself -- proving membership is resolved via Grade+Section, not just Name.
    /// Mirrors GreetByIdHandlerTests.SeedTeacherWithGradeSectionClass exactly.
    private async Task SeedTeacherWithGradeSectionClass(
        Guid tenantId, Guid teacherUserId, string className, string grade, string section)
    {
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            var teacherId = Guid.NewGuid();
            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\") VALUES (@teacherUserId, @tenantId)",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"UserId\") VALUES (@teacherId, @tenantId, 'Meena', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT INTO "dbo"."Classes" ("Id", "TenantId", "Name", "Grade", "Section", "StudentCount", "ClassTeacherId")
                VALUES (@classId, @tenantId, @className, @grade, @section, 0, @teacherId)
                """,
                new { classId, tenantId, teacherId, className, grade, section });
        });
    }

    private static async Task<Guid> InsertStudent(
        NpgsqlConnection conn, Guid tenantId, string name, string grade, string section, string classLabel)
    {
        var id = Guid.NewGuid();
        var admissionNo = $"ADM-{Guid.NewGuid():N}"[..20];
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Grade", "Section", "ClassLabel", "Status")
            VALUES (@id, @tenantId, @admissionNo, @name, @grade, @section, @classLabel, 'active')
            """,
            new { id, tenantId, admissionNo, name, grade, section, classLabel });
        return id;
    }

    private static async Task InsertTeacher(NpgsqlConnection conn, Guid tenantId, string name) =>
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\") VALUES (@id, @tenantId, @name)",
            new { id = Guid.NewGuid(), tenantId, name });

    private static async Task InsertStaff(NpgsqlConnection conn, Guid tenantId, string name) =>
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\") VALUES (@id, @tenantId, @name)",
            new { id = Guid.NewGuid(), tenantId, name });

    private static async Task<Guid> InsertAdmin(
        NpgsqlConnection conn, Guid tenantId, string name, string email, string role = "school.admin")
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Email\", \"Name\", \"Status\") VALUES (@id, @tenantId, @email, @name, 'active')",
            new { id, tenantId, email, name });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"UserRoles\" (\"UserId\", \"Role\") VALUES (@id, @role)", new { id, role });
        return id;
    }

    /// Runs the REAL AiSearchAuthorizationService.AuthorizeAsync against the ambient tenant/user,
    /// exactly as the request pipeline would after JWT validation.
    private static async Task<AiAuthorizationResult> Authorize(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string[] roles)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var svc = scope.ServiceProvider.GetRequiredService<IAiSearchAuthorizationService>();
        var filters = new AiSearchFilters(null, null, null, null, false);
        return await svc.AuthorizeAsync(Intent, filters, roles);
    }

    private static async Task<IReadOnlyList<PersonMatch>> Resolve(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, AiAuthorizationResult auth, string name)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var resolver = scope.ServiceProvider.GetRequiredService<IPersonResolver>();
        return await resolver.ResolveAsync(name, auth);
    }

    private static async Task<bool> IsStillInScope(
        WebApplicationFactory<Program> app, Guid tenantId, Guid teacherUserId,
        Guid studentId, IReadOnlyList<string> allowedClassNames)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, teacherUserId, isPlatform: false);
        var resolver = scope.ServiceProvider.GetRequiredService<IPersonResolver>();
        return await resolver.IsStillInTeacherScopeAsync(studentId, allowedClassNames);
    }

    [Fact]
    public async Task Parent_search_only_ever_reaches_their_own_linked_children_never_teachers_or_staff()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-PR1-{Guid.NewGuid():N}"[..20],
            name = "Rahul Verma",
            grade = "8",
            section = "A",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);
        var childId = created.GetProperty("id").GetGuid();

        await SeedInTenant(tenantId, async conn =>
        {
            await InsertTeacher(conn, tenantId, "Rahul Sharma");
            await InsertStaff(conn, tenantId, "Rahul Khan");
        });

        var parentId = await ParentUserId(parentEmail, tenantId);

        var auth = await Authorize(app, tenantId, parentId, [Policies.StudentOrParent]);
        auth.Allowed.Should().BeTrue();
        auth.Unrestricted.Should().BeFalse();

        var matches = await Resolve(app, tenantId, parentId, auth, "Rahul");

        matches.Should().ContainSingle();
        matches[0].Id.Should().Be(childId);
        matches[0].Type.Should().Be("student");
        matches[0].Name.Should().Be("Rahul Verma");
    }

    [Fact]
    public async Task Teacher_search_is_scoped_to_students_in_their_own_classes_via_GradeSection_membership()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        // Classes.Name is deliberately free text that does NOT compact down to "8A" -- only the
        // class's own Grade/Section columns line up with the student's Grade/Section.
        await SeedTeacherWithGradeSectionClass(tenantId, teacherUserId, "Section Eight A", "8", "A");

        Guid taughtStudentId = default;
        await SeedInTenant(tenantId, async conn =>
        {
            taughtStudentId = await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
            await InsertStudent(conn, tenantId, "Rahul Khan", "9", "B", "9-B");
        });

        var auth = await Authorize(app, tenantId, teacherUserId, [Policies.Teacher]);
        auth.Allowed.Should().BeTrue();
        auth.Unrestricted.Should().BeFalse();
        auth.AllowedClassNames.Should().NotBeNull();

        var matches = await Resolve(app, tenantId, teacherUserId, auth, "Rahul");

        matches.Should().ContainSingle();
        matches[0].Id.Should().Be(taughtStudentId);
        matches[0].Name.Should().Be("Rahul Verma");
    }

    [Fact]
    public async Task Unrestricted_search_fans_out_across_all_four_sources_and_finds_all_matches()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        await SeedInTenant(tenantId, async conn =>
        {
            await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
            await InsertTeacher(conn, tenantId, "Rahul Sharma");
            await InsertStaff(conn, tenantId, "Rahul Khan");
            await InsertAdmin(conn, tenantId, "Rahul Gupta", $"gupta{Guid.NewGuid():N}@school.test");
        });

        var callerId = Guid.NewGuid();
        var auth = await Authorize(app, tenantId, callerId, [Policies.SchoolAdmin]);
        auth.Allowed.Should().BeTrue();
        auth.Unrestricted.Should().BeTrue();

        var matches = await Resolve(app, tenantId, callerId, auth, "Rahul");

        matches.Should().HaveCount(4);
        matches.Should().ContainSingle(m => m.Type == "student" && m.Name == "Rahul Verma");
        matches.Should().ContainSingle(m => m.Type == "teacher" && m.Name == "Rahul Sharma");
        matches.Should().ContainSingle(m => m.Type == "staff" && m.Name == "Rahul Khan");
        matches.Should().ContainSingle(m => m.Type == "admin" && m.Name == "Rahul Gupta");
    }

    [Fact]
    public async Task Cross_tenant_same_name_students_never_appear_together()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid tenantAStudentId = default;
        await SeedInTenant(tenantA, async conn =>
        {
            tenantAStudentId = await InsertStudent(conn, tenantA, "Rahul Verma", "8", "A", "8-A");
        });
        await SeedInTenant(tenantB, async conn =>
        {
            await InsertStudent(conn, tenantB, "Rahul Verma", "8", "A", "8-A");
        });

        var callerId = Guid.NewGuid();
        var auth = await Authorize(app, tenantA, callerId, [Policies.SchoolAdmin]);
        auth.Unrestricted.Should().BeTrue();

        var matches = await Resolve(app, tenantA, callerId, auth, "Rahul");

        matches.Should().ContainSingle();
        matches[0].Id.Should().Be(tenantAStudentId);
    }

    [Fact]
    public async Task Two_admins_with_the_same_name_and_role_get_a_masked_email_tie_breaker_in_Detail()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        const string email1 = "rahul.s@school.test";
        const string email2 = "rahul.sharma2@school.test";

        await SeedInTenant(tenantId, async conn =>
        {
            await InsertAdmin(conn, tenantId, "Rahul Sharma", email1);
            await InsertAdmin(conn, tenantId, "Rahul Sharma", email2);
        });

        var callerId = Guid.NewGuid();
        var auth = await Authorize(app, tenantId, callerId, [Policies.SchoolAdmin]);

        var matches = await Resolve(app, tenantId, callerId, auth, "Rahul");

        matches.Should().HaveCount(2);
        matches.Should().OnlyContain(m => m.Type == "admin");
        matches.Should().OnlyContain(m => m.Detail != null && !m.Detail.Contains(email1) && !m.Detail.Contains(email2));
        matches.Should().Contain(m => m.Detail!.StartsWith("Admin (r") && m.Detail.EndsWith("@school.test)"));
    }

    [Fact]
    public async Task A_single_admin_with_no_name_collision_gets_the_plain_role_label_as_Detail()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        await SeedInTenant(tenantId, async conn =>
        {
            await InsertAdmin(conn, tenantId, "Rahul Gupta", $"gupta{Guid.NewGuid():N}@school.test");
        });

        var callerId = Guid.NewGuid();
        var auth = await Authorize(app, tenantId, callerId, [Policies.SchoolAdmin]);

        var matches = await Resolve(app, tenantId, callerId, auth, "Rahul");

        matches.Should().ContainSingle();
        matches[0].Detail.Should().Be("Admin");
    }

    [Fact]
    public async Task IsStillInTeacherScopeAsync_is_true_for_a_student_in_a_class_the_teacher_teaches()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await SeedTeacherWithGradeSectionClass(tenantId, teacherUserId, "Section Eight A", "8", "A");

        Guid studentId = default;
        await SeedInTenant(tenantId, async conn =>
        {
            studentId = await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
        });

        var auth = await Authorize(app, tenantId, teacherUserId, [Policies.Teacher]);
        auth.AllowedClassNames.Should().NotBeNull();

        var stillInScope = await IsStillInScope(app, tenantId, teacherUserId, studentId, auth.AllowedClassNames!);

        stillInScope.Should().BeTrue();
    }

    [Fact]
    public async Task IsStillInTeacherScopeAsync_is_false_once_the_student_has_moved_to_a_different_class()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await SeedTeacherWithGradeSectionClass(tenantId, teacherUserId, "Section Eight A", "8", "A");

        Guid studentId = default;
        await SeedInTenant(tenantId, async conn =>
        {
            studentId = await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
        });

        var auth = await Authorize(app, tenantId, teacherUserId, [Policies.Teacher]);
        auth.AllowedClassNames.Should().NotBeNull();

        // Student moves to a different class the teacher does not teach.
        await SeedInTenant(tenantId, conn => conn.ExecuteAsync(
            "UPDATE \"dbo\".\"Students\" SET \"Grade\" = '9', \"Section\" = 'B', \"ClassLabel\" = '9-B' WHERE \"Id\" = @studentId",
            new { studentId }));

        var stillInScope = await IsStillInScope(app, tenantId, teacherUserId, studentId, auth.AllowedClassNames!);

        stillInScope.Should().BeFalse();
    }
}
