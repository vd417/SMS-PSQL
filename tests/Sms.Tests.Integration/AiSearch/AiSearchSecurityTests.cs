using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// <summary>
/// A deterministic classification result, substituted for the live Claude-backed
/// <see cref="IAiClassificationClient"/> via <c>ConfigureTestServices</c>. Every security test below
/// scripts the classifier to the WORST-CASE output — the intent and filters a maximally hostile (or
/// simply wrong) LLM response could produce — so what is actually under test is the backend's
/// authorization/clamping behaviour when handed that output, never Claude's own accuracy.
/// </summary>
public sealed class ScriptedClassificationClient(AiClassificationResult result) : IAiClassificationClient
{
    public Task<AiClassificationResult> ClassifyAsync(string query, AiConversationHint? hint = null, CancellationToken ct = default) =>
        Task.FromResult(result);
}

/// <summary>
/// End-to-end security regression suite for AI global search (spec §12). Every scenario goes over
/// real HTTP through <c>POST /v1/ai/search</c>, with a real JWT and real seeded SQL rows, so the real
/// tenancy (RLS + <c>ITenantContext</c>), <see cref="AiSearchAuthorizationService"/> clamping,
/// <see cref="AiIntentAccessRules"/> role gate, write refusal and feature gate are all exercised.
/// The ONLY substituted component is the classifier, which would otherwise be a live LLM call.
/// </summary>
[Collection("sql")]
public class AiSearchSecurityTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    /// The exact fixed refusal string AiAnswerTemplateService renders for an English write attempt.
    private const string WriteRefusal = "I can only search and display information. I cannot modify school data.";

    private WebApplicationFactory<Program> App(Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            if (configureServices is not null)
                b.ConfigureTestServices(configureServices);
        });

    /// Registers the scripted classifier last, so it wins over the real Claude-backed registration.
    private WebApplicationFactory<Program> AppClassifying(AiClassificationResult result) =>
        App(s => s.AddScoped<IAiClassificationClient>(_ => new ScriptedClassificationClient(result)));

    private static string Token(Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        return jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
    }

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenantId) =>
        AsUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);

    private static HttpClient AsUser(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(tenantId, userId, role));
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    /// POSTs the search and returns the whole envelope (success/intent/answer/data/...), asserting
    /// the HTTP status first so a failure message carries the real body.
    private static async Task<JsonElement> Search(
        HttpClient client, string query, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await client.PostAsJsonAsync("/v1/ai/search", new { query });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await work(conn);
    }

    private async Task<T> Query<T>(Func<NpgsqlConnection, Task<T>> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        return await work(conn);
    }

    private async Task<Guid> ParentUserId(string email, Guid tenantId) => await Query(conn =>
        conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId
              AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@email)))
            """,
            new { email, tenantId }));

    private async Task<Guid> StudentUserId(string admissionNo, Guid tenantId) => await Query(conn =>
        conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId
              AND LOWER(LTRIM(RTRIM(StudentId))) = LOWER(LTRIM(RTRIM(@admissionNo)))
            """,
            new { admissionNo, tenantId }));

    /// Inserts <paramref name="count"/> active students into one tenant's ClassLabel/Section.
    private async Task SeedClass(Guid tenantId, string classLabel, string section, string grade, int count) =>
        await Seed(async conn =>
        {
            for (var i = 0; i < count; i++)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                    VALUES (NEWID(), @tenantId, @adm, @name, @grade, @section, @classLabel, N'active')
                    """,
                    new
                    {
                        tenantId,
                        adm = $"ADM-SEC-{Guid.NewGuid():N}"[..20],
                        name = $"Pupil {classLabel} {i}",
                        grade,
                        section,
                        classLabel,
                    });
            }
        });

    private async Task SeedTeacherOf(Guid tenantId, Guid teacherUserId, string className) =>
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
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
                VALUES (@classId, @tenantId, @className, 0, @teacherId)
                """,
                new { classId, tenantId, teacherId, className });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId)
                VALUES (@tenantId, 'Mon', 1, N'Math', @classId, @className, @teacherId)
                """,
                new { tenantId, classId, teacherId, className });
        });

    private static AiSearchFilters Filters(
        string? studentName = null, string? className = null, string? section = null,
        string? dateExpression = "today", bool targetSelf = false) =>
        new(studentName, className, section, dateExpression, targetSelf);

    /// <summary>
    /// Two tenants both have a class literally named "8A". The class name reaches the aggregate query
    /// from the LLM-extracted filter, but the tenant id never does — it is re-derived from the JWT — so
    /// the answer must be tenant A's 10 students, never tenant B's 5 and never the combined 15.
    /// </summary>
    [Fact]
    public async Task Tenant_isolation_ClassAttendance_never_returns_another_tenants_students()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantA, tier: "platinum");
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantB, tier: "platinum");
        await SeedClass(tenantA, "8A", "A", "8", count: 10);
        await SeedClass(tenantB, "8A", "A", "8", count: 5);

        await using var app = AppClassifying(
            new AiClassificationResult("en", "ClassAttendance", Filters(className: "8A", section: "A")));

        var body = await Search(Admin(app, tenantA), "8A ki attendance kya hai?");

        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("intent").GetString().Should().Be("ClassAttendance");
        var data = body.GetProperty("data");
        data.GetProperty("total").GetInt32().Should().Be(10);
        data.GetProperty("total").GetInt32().Should().NotBe(15);
        data.GetProperty("total").GetInt32().Should().NotBe(5);
    }

    /// <summary>
    /// A parent linked only to "Aisha" asks about "Rahul", a real student in the same tenant. The
    /// authorization service resolves names only over the caller's own ParentStudentLinks, so nothing
    /// resolves and the handler must answer "no match" — never Rahul's real attendance percentage.
    /// </summary>
    [Fact]
    public async Task Parent_security_cannot_resolve_an_unlinked_students_attendance()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        await using var app = AppClassifying(
            new AiClassificationResult("en", "StudentAttendance", Filters(studentName: "Rahul")));

        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-AI-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

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
        var body = await Search(
            AsUser(app, tenantId, parentId, Policies.StudentOrParent), "Rahul ki attendance kitni hai?");

        body.GetProperty("intent").GetString().Should().Be("Unsupported");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        // Nothing about the unlinked student may appear anywhere in the envelope.
        body.GetRawText().Should().NotContain("Rahul");
        body.GetRawText().Should().NotContain(rahul.GetProperty("id").GetGuid().ToString());
    }

    /// <summary>
    /// "My attendance" must always resolve to the CALLER'S OWN record. Even when the classifier also
    /// extracts another student's name alongside <c>TargetSelf</c>, the name is discarded.
    /// </summary>
    [Fact]
    public async Task Student_security_targetSelf_ignores_any_other_students_name_filter()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        await using var app = AppClassifying(new AiClassificationResult(
            "en", "StudentAttendance", Filters(studentName: "SomeoneElse", targetSelf: true)));

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

        var someoneElse = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-OTH-{Guid.NewGuid():N}"[..20],
            name = "SomeoneElse Gupta",
            grade = "VI",
            section = "B",
            roll = 2,
        }), HttpStatusCode.Created);

        var studentUserId = await StudentUserId(admissionNo, tenantId);
        var body = await Search(
            AsUser(app, tenantId, studentUserId, Policies.StudentOrParent), "meri attendance kitni hai?");

        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("intent").GetString().Should().Be("StudentAttendance");
        var data = body.GetProperty("data");
        data.GetProperty("student_id").GetGuid().Should().Be(me.GetProperty("id").GetGuid());
        data.GetProperty("student_id").GetGuid().Should().NotBe(someoneElse.GetProperty("id").GetGuid());
        data.GetProperty("name").GetString().Should().Be("Nikhil Rao");
        body.GetRawText().Should().NotContain("SomeoneElse");
    }

    /// <summary>
    /// A teacher assigned (via TimetableSlots) only to 8A asks about 9B, which really exists and really
    /// has students. Authorization clamps ClassName/Section back to null, and the handler must degrade
    /// to Unsupported rather than answering with 9B's real numbers.
    /// </summary>
    [Fact]
    public async Task Teacher_security_cannot_see_a_class_they_do_not_teach()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SeedTeacherOf(tenantId, teacherUserId, "8A");
        await SeedClass(tenantId, "9B", "B", "9", count: 7);

        await using var app = AppClassifying(
            new AiClassificationResult("en", "ClassAttendance", Filters(className: "9B", section: "B")));

        var body = await Search(
            AsUser(app, tenantId, teacherUserId, Policies.Teacher), "9B ki attendance batao");

        body.GetProperty("intent").GetString().Should().Be("Unsupported");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetRawText().Should().NotContain("9B");
    }

    /// <summary>
    /// The role gate is intent-level and independent of any data scoping: "staff" is not in
    /// <c>AiIntentAccessRules["DashboardSummary"]</c>, so the request is refused outright.
    /// </summary>
    [Fact]
    public async Task RBAC_staff_role_cannot_reach_DashboardSummary()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        await using var app = AppClassifying(
            new AiClassificationResult("en", "DashboardSummary", Filters()));

        var body = await Search(
            AsUser(app, tenantId, Guid.NewGuid(), Policies.Staff), "school ka dashboard dikhao");

        body.GetProperty("intent").GetString().Should().Be("Forbidden");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("count").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// Every mutation phrasing the classifier flags as a write must be refused with the fixed refusal
    /// string, BEFORE authorization runs — so even a school admin (who could legitimately perform the
    /// mutation through a real write endpoint) is refused here. This validates the BACKEND'S handling
    /// of a "WriteRequestDetected" classification, not Claude's own classification accuracy, which the
    /// spec puts out of scope for automated tests.
    /// </summary>
    [Theory]
    [InlineData("Rahul ki attendance present kar do")]
    [InlineData("Delete all students")]
    [InlineData("Mark Rahul present")]
    public async Task Write_protection_blocks_every_mutation_phrasing(string phrasing)
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SeedClass(tenantId, "8A", "A", "8", count: 3);

        await using var app = AppClassifying(new AiClassificationResult(
            "en", "WriteRequestDetected", Filters(studentName: "Rahul", className: "8A")));

        var before = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId }));

        // The most privileged caller available: a refusal here proves the block is not role-derived.
        var body = await Search(Admin(app, tenantId), phrasing);

        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("intent").GetString().Should().Be("WriteBlocked");
        body.GetProperty("answer").GetString().Should().Be(WriteRefusal);
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);

        var after = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId }));
        after.Should().Be(before);
    }

    /// <summary>
    /// SQL-injection-shaped text is just text: it is never concatenated into a query, and the only
    /// thing that ever reaches a repository is the structured, authorization-clamped filter set. The
    /// row-count assertion is the load-bearing one — a 200 with "Unsupported" would still be a failure
    /// if the DELETE had executed.
    /// </summary>
    [Fact]
    public async Task Sql_injection_style_query_text_is_treated_as_ordinary_unclassifiable_text()
    {
        const string injection = "Show students; DELETE FROM Students";
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SeedClass(tenantId, "7C", "C", "7", count: 4);

        // The classifier itself is scripted to echo the raw injection text back as a filter, so the
        // hostile string travels the whole pipeline rather than being dropped at the front door.
        await using var app = AppClassifying(
            new AiClassificationResult("en", "Unsupported", Filters(studentName: injection, className: injection)));

        var tenantBefore = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId }));
        var globalBefore = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students"));
        tenantBefore.Should().Be(4);

        var body = await Search(Admin(app, tenantId), injection);

        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("intent").GetString().Should().Be("Unsupported");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);

        var tenantAfter = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students WHERE TenantId = @tenantId", new { tenantId }));
        var globalAfter = await Query(conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Students"));
        tenantAfter.Should().Be(tenantBefore);
        globalAfter.Should().Be(globalBefore);

        // Belt and braces: the tenant's roster is still readable and still complete over real HTTP.
        var roster = await Data(
            await Admin(app, tenantId).GetAsync("/v1/students"), HttpStatusCode.OK);
        roster.GetArrayLength().Should().Be(4);
    }

    /// <summary>
    /// The plan-tier gate runs first in the pipeline — before validation and before any LLM call — so a
    /// tenant on a tier without <c>ai_search</c> gets a 403 FeatureNotEnabled and never a search result.
    /// </summary>
    [Fact]
    public async Task Feature_gating_blocks_tenants_without_the_ai_search_plan_feature()
    {
        var tenantId = Guid.NewGuid();
        // "silver" grants sis/attendance/exams/... but ai_search is Platinum-only (see TierFeatures).
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "silver");
        await SeedClass(tenantId, "8A", "A", "8", count: 3);

        await using var app = AppClassifying(
            new AiClassificationResult("en", "ClassAttendance", Filters(className: "8A", section: "A")));

        var body = await Search(Admin(app, tenantId), "8A ki attendance kya hai?", HttpStatusCode.Forbidden);

        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetProperty("code").GetString().Should().Be("FeatureNotEnabled");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("intent").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// End-to-end GreetById over real HTTP: the classifier puts the scanned admission number into
    /// filters.studentName (per its updated prompt), and an admin-like caller resolves it to the real
    /// student's name inside a time-of-day greeting.
    /// </summary>
    [Fact]
    public async Task GreetById_admin_resolves_a_scanned_admission_number_over_real_http()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var admissionNo = $"ADM-GBHTTP-{Guid.NewGuid():N}"[..20];

        await using var app = AppClassifying(
            new AiClassificationResult("en", "GreetById", Filters(studentName: admissionNo, dateExpression: null)));

        var admin = Admin(app, tenantId);
        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "Kabir Mehta",
            grade = "VII",
            section = "C",
            roll = 3,
        }), HttpStatusCode.Created);

        var body = await Search(admin, admissionNo);

        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("intent").GetString().Should().Be("GreetById");
        body.GetProperty("answer").GetString().Should().Contain("Kabir Mehta");
    }

    /// <summary>
    /// A parent scans a real student's admission number who is not their own child. GreetById must
    /// never leak that student's name — the scope re-derived from the parent's own
    /// ParentStudentLinks contains only their linked children, exactly like every other intent.
    /// </summary>
    [Fact]
    public async Task GreetById_parent_security_cannot_resolve_an_unlinked_students_admission_number()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var strangersAdmissionNo = $"ADM-GBH2-{Guid.NewGuid():N}"[..20];

        await using var app = AppClassifying(new AiClassificationResult(
            "en", "GreetById", Filters(studentName: strangersAdmissionNo, dateExpression: null)));

        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-GBH1-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var stranger = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = strangersAdmissionNo,
            name = "Rahul Verma",
            grade = "V",
            section = "A",
            roll = 2,
            guardian_email = $"other{Guid.NewGuid():N}@home.test",
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);
        var body = await Search(AsUser(app, tenantId, parentId, Policies.StudentOrParent), strangersAdmissionNo);

        body.GetProperty("intent").GetString().Should().Be("Unsupported");
        body.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetRawText().Should().NotContain("Rahul");
        body.GetRawText().Should().NotContain(stranger.GetProperty("id").GetGuid().ToString());
    }
}
