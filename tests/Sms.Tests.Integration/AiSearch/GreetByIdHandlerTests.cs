using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Sis;
using Sms.Modules.Academics.Data;
using Sms.Modules.Staffing.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises GreetByIdHandler through the REAL AiSearchAuthorizationService.AuthorizeAsync
/// pipeline (not hand-crafted auth results) wherever a role's scope-resolution matters, proving
/// the full authorization-service -> handler chain never leaks a name the caller is not
/// authorized to see, even when the exact code they scanned genuinely exists in the tenant (or in
/// another tenant entirely).
[Collection("sql")]
public class GreetByIdHandlerTests(PostgresFixture fx)
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
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await work(conn);
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

    /// Runs the REAL AiSearchAuthorizationService.AuthorizeAsync against the ambient tenant/user,
    /// exactly as the request pipeline would after JWT validation.
    private static async Task<AiAuthorizationResult> Authorize(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId,
        AiSearchFilters filters, string[] roles)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var svc = scope.ServiceProvider.GetRequiredService<IAiSearchAuthorizationService>();
        return await svc.AuthorizeAsync("GreetById", filters, roles);
    }

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, AiAuthorizationResult auth,
        string language = "en", TimeProvider? clock = null)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var handler = new GreetByIdHandler(
            scope.ServiceProvider.GetRequiredService<ISisService>(),
            scope.ServiceProvider.GetRequiredService<TeacherRepository>(),
            scope.ServiceProvider.GetRequiredService<StaffRepository>(),
            scope.ServiceProvider.GetRequiredService<ClassRepository>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>(),
            clock ?? scope.ServiceProvider.GetRequiredService<TimeProvider>());
        return await handler.HandleAsync(auth, language, 1, 20);
    }

    /// End-to-end: Authorize (real) then Handle (real), for a caller with the given roles.
    private static async Task<AiSearchResponse> AuthorizeAndHandle(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string code, string[] roles,
        TimeProvider? clock = null)
    {
        var filters = new AiSearchFilters(code, null, null, null, false);
        var auth = await Authorize(app, tenantId, userId, filters, roles);
        auth.Allowed.Should().BeTrue();
        return await Handle(app, tenantId, userId, auth, clock: clock);
    }

    private async Task SeedTeacherWithClass(
        Guid tenantId, Guid teacherUserId, string taughtClassLabel)
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
                INSERT INTO "dbo"."Classes" ("Id", "TenantId", "Name", "StudentCount", "ClassTeacherId")
                VALUES (@classId, @tenantId, @taughtClassLabel, 0, @teacherId)
                """,
                new { classId, tenantId, teacherId, taughtClassLabel });
            await conn.ExecuteAsync(
                """
                INSERT INTO "dbo"."TimetableSlots" ("TenantId", "Day", "Period", "Subject", "ClassId", "ClassName", "TeacherId")
                VALUES (@tenantId, 'Mon', 1, 'Math', @classId, @taughtClassLabel, @teacherId)
                """,
                new { tenantId, classId, teacherId, taughtClassLabel });
        });
    }

    /// Seeds a teacher whose taught class has a free-text Name that does NOT already look like a
    /// compacted "Grade-Section" label (e.g. "Section Eight A"), alongside a real Grade/Section on
    /// the class row itself — proving membership is resolved via Grade+Section, not just Name.
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
        NpgsqlConnection conn, Guid tenantId, string admissionNo, string name, string classLabel)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Grade", "Section", "ClassLabel", "Status")
            VALUES (@id, @tenantId, @admissionNo, @name, '8', 'A', @classLabel, 'active')
            """,
            new { id, tenantId, admissionNo, name, classLabel });
        return id;
    }

    private static async Task InsertTeacher(
        NpgsqlConnection conn, Guid tenantId, string name, string employeeCode) =>
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Teachers\" (\"Id\", \"TenantId\", \"Name\", \"EmployeeCode\") VALUES (@id, @tenantId, @name, @employeeCode)",
            new { id = Guid.NewGuid(), tenantId, name, employeeCode });

    private static async Task InsertStaff(
        NpgsqlConnection conn, Guid tenantId, string name, string employeeCode) =>
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Staff\" (\"Id\", \"TenantId\", \"Name\", \"EmployeeCode\") VALUES (@id, @tenantId, @name, @employeeCode)",
            new { id = Guid.NewGuid(), tenantId, name, employeeCode });

    [Fact]
    public async Task Parent_scanning_their_own_childs_admission_number_resolves_with_the_correct_name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var admissionNo = $"ADM-GB-{Guid.NewGuid():N}"[..20];

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var response = await AuthorizeAndHandle(app, tenantId, parentId, admissionNo, [Policies.StudentOrParent]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Aisha Khan");
        response.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task Parent_scanning_a_different_real_students_admission_number_gets_no_match_and_never_leaks_the_name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-GBP-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var strangersAdmissionNo = $"ADM-GBS-{Guid.NewGuid():N}"[..20];
        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = strangersAdmissionNo,
            name = "Rahul Verma",
            grade = "V",
            section = "A",
            roll = 2,
            guardian_email = $"other{Guid.NewGuid():N}@home.test",
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);

        var response = await AuthorizeAndHandle(
            app, tenantId, parentId, strangersAdmissionNo, [Policies.StudentOrParent]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Rahul");
    }

    [Fact]
    public async Task Teacher_scanning_a_student_from_a_class_they_teach_resolves()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var admissionNo = $"ADM-GBT1-{Guid.NewGuid():N}"[..20];

        await SeedTeacherWithClass(tenantId, teacherUserId, "8A");
        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Taught Student", "8A"));

        var response = await AuthorizeAndHandle(app, tenantId, teacherUserId, admissionNo, [Policies.Teacher]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Taught Student");
    }

    [Fact]
    public async Task Teacher_scanning_a_student_from_a_class_they_do_not_teach_gets_no_match_and_never_leaks_the_name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var admissionNo = $"ADM-GBT2-{Guid.NewGuid():N}"[..20];

        await SeedTeacherWithClass(tenantId, teacherUserId, "8A");
        // Real student, real admission number, but in a class this teacher does not teach.
        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Untaught Student", "9B"));

        var response = await AuthorizeAndHandle(app, tenantId, teacherUserId, admissionNo, [Policies.Teacher]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Untaught");
    }

    [Fact]
    public async Task Admin_scanning_a_students_admission_number_resolves_via_the_unrestricted_path()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admissionNo = $"ADM-GBA-{Guid.NewGuid():N}"[..20];

        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Priya Nair", "8A"));

        var response = await AuthorizeAndHandle(app, tenantId, Guid.NewGuid(), admissionNo, [Policies.SchoolAdmin]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Priya Nair");
    }

    [Fact]
    public async Task Admin_scanning_a_staff_employee_code_resolves_as_staff_not_student()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var employeeCode = $"EMP-GBS-{Guid.NewGuid():N}"[..20];

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await InsertStaff(conn, tenantId, "Gita Sharma", employeeCode);
        });

        var response = await AuthorizeAndHandle(app, tenantId, Guid.NewGuid(), employeeCode, [Policies.SchoolAdmin]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Gita Sharma");
        var type = response.Data!.GetType();
        type.GetProperty("type")!.GetValue(response.Data).Should().Be("staff");
    }

    [Fact]
    public async Task Admin_scanning_a_teacher_employee_code_resolves_as_teacher_type_via_teacher_repository()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var employeeCode = $"EMP-GBT-{Guid.NewGuid():N}"[..20];

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await InsertTeacher(conn, tenantId, "Meena Rao", employeeCode);
        });

        var response = await AuthorizeAndHandle(app, tenantId, Guid.NewGuid(), employeeCode, [Policies.SchoolAdmin]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Meena Rao");
        var type = response.Data!.GetType();
        type.GetProperty("type")!.GetValue(response.Data).Should().Be("teacher");
    }

    [Fact]
    public async Task Parent_scanning_a_real_staff_employee_code_gets_no_match()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var employeeCode = $"EMP-GBP-{Guid.NewGuid():N}"[..20];

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-GBPP-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await InsertStaff(conn, tenantId, "Gita Sharma", employeeCode);
        });

        var parentId = await ParentUserId(parentEmail, tenantId);

        var response = await AuthorizeAndHandle(app, tenantId, parentId, employeeCode, [Policies.StudentOrParent]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Gita");
    }

    [Fact]
    public async Task Teacher_scanning_a_real_staff_employee_code_gets_no_match()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var employeeCode = $"EMP-GBTC-{Guid.NewGuid():N}"[..20];

        await SeedTeacherWithClass(tenantId, teacherUserId, "8A");
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await InsertStaff(conn, tenantId, "Gita Sharma", employeeCode);
        });

        var response = await AuthorizeAndHandle(app, tenantId, teacherUserId, employeeCode, [Policies.Teacher]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Gita");
    }

    [Fact]
    public async Task Cross_tenant_admission_number_real_in_another_tenant_is_never_resolved()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var admissionNo = $"ADM-GBX-{Guid.NewGuid():N}"[..20];

        await Seed(async conn => await InsertStudent(conn, tenantB, admissionNo, "Someone Else", "8A"));

        var response = await AuthorizeAndHandle(app, tenantA, Guid.NewGuid(), admissionNo, [Policies.SchoolAdmin]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Someone Else");
    }

    [Fact]
    public async Task A_code_matching_nothing_anywhere_returns_a_clean_not_found_response()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var response = await AuthorizeAndHandle(
            app, tenantId, Guid.NewGuid(), $"NOPE-{Guid.NewGuid():N}", [Policies.SchoolAdmin]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();

        var templates = app.Services.GetRequiredService<IAiAnswerTemplateService>();
        response.Answer.Should().Be(templates.RenderNoMatch("en"));
    }

    [Fact]
    public async Task Greeting_uses_school_local_IST_time_not_raw_UTC_hour()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admissionNo = $"ADM-GBIST-{Guid.NewGuid():N}"[..20];

        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Priya Nair", "8A"));

        // 08:00 UTC is morning in UTC, but 13:30 IST (UTC+5:30) — afternoon. If the handler used raw
        // UTC hour it would say "Good morning"; using school-local (IST) time it must say "Good afternoon".
        var utcMorningButIstAfternoon = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(utcMorningButIstAfternoon);

        var response = await AuthorizeAndHandle(
            app, tenantId, Guid.NewGuid(), admissionNo, [Policies.SchoolAdmin], clock);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Good afternoon");
        response.Answer.Should().NotContain("Good morning");
    }

    [Fact]
    public async Task Teacher_can_resolve_a_student_via_GradeSection_even_when_the_classs_Name_is_not_a_compacted_label()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var admissionNo = $"ADM-GBGS-{Guid.NewGuid():N}"[..20];

        // Classes.Name is deliberately free text that does NOT compact down to "8A" — only the
        // class's own Grade/Section columns line up with the student's Grade/Section.
        await SeedTeacherWithGradeSectionClass(tenantId, teacherUserId, "Section Eight A", "8", "A");
        // ClassLabel is the compact "8-A" form, distinct from the free-text class Name.
        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Grade Section Student", "8-A"));

        var response = await AuthorizeAndHandle(app, tenantId, teacherUserId, admissionNo, [Policies.Teacher]);

        response.Intent.Should().Be("GreetById");
        response.Answer.Should().Contain("Grade Section Student");
    }

    [Fact]
    public async Task Parent_with_zero_ParentStudentLinks_scanning_a_real_students_admission_number_gets_no_match()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var otherAdmissionNo = $"ADM-GBZP-{Guid.NewGuid():N}"[..20];

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = otherAdmissionNo,
            name = "Someone Elses Child",
            grade = "IV",
            section = "B",
            roll = 1,
        }), HttpStatusCode.Created);

        // A parent user that exists but has ZERO ParentStudentLinks rows.
        var zeroScopeParentId = Guid.NewGuid();
        await Seed(async conn => await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\") VALUES (@id, @tenantId)",
            new { id = zeroScopeParentId, tenantId }));

        var response = await AuthorizeAndHandle(
            app, tenantId, zeroScopeParentId, otherAdmissionNo, [Policies.StudentOrParent]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Someone Elses Child");
    }

    [Fact]
    public async Task Teacher_role_with_no_matching_Teachers_row_scanning_a_real_students_admission_number_gets_no_match()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admissionNo = $"ADM-GBZT-{Guid.NewGuid():N}"[..20];

        await Seed(async conn => await InsertStudent(conn, tenantId, admissionNo, "Real Student", "8A"));

        // A caller with the school.teacher role but no row in dbo.Teachers — AllowedClassNames
        // resolves to an empty (non-null) list.
        var noTeacherRowUserId = Guid.NewGuid();

        var response = await AuthorizeAndHandle(
            app, tenantId, noTeacherRowUserId, admissionNo, [Policies.Teacher]);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.Answer.Should().NotContain("Real Student");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
