using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Modules.AiSearch.Data;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises ClassAttendanceHandler's degrade-gracefully path directly (no HTTP layer needed yet —
/// the AI search controller lands in Task 12). Both tests assert the handler never queries when the
/// class name in play is missing, whether that's because the caller never asked for a class, or
/// because the authorization service already clamped a disallowed class name back to null.
[Collection("sql")]
public class ClassAttendanceHandlerTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    /// Resolves a ClassAttendanceHandler wired to the real repository, with the ambient
    /// ITenantContext set exactly as the request pipeline would after JWT validation.
    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new ClassAttendanceHandler(
            scope.ServiceProvider.GetRequiredService<AiAttendanceAggregateRepository>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    private async Task Seed(Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    /// Runs the REAL AiSearchAuthorizationService.AuthorizeAsync against the ambient tenant/user,
    /// exactly as the request pipeline would after JWT validation.
    private static async Task<AiAuthorizationResult> Authorize(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId,
        string intent, AiSearchFilters filters, string[] roles)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var svc = scope.ServiceProvider.GetRequiredService<IAiSearchAuthorizationService>();
        return await svc.AuthorizeAsync(intent, filters, roles);
    }

    [Fact]
    public async Task Missing_class_name_returns_Unsupported_instead_of_throwing()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        // No class name was ever asked for (e.g. classifier extracted no ClassName filter).
        var filters = new AiSearchFilters(null, null, null, "today", false);
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "ClassAttendance", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: null,
            ClampedFilters: filters, Unrestricted: true, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Teachers_class_filter_that_was_clamped_to_null_by_authorization_also_returns_Unsupported()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        // Simulates the "teacher asked about a class they don't teach" path from Task 7 — the
        // authorization service already stripped ClassName/Section back to null, and this teacher's
        // AllowedClassNames is empty (a school.teacher JWT with no matching dbo.Teachers row): a real
        // zero-scope answer, not "no filter". The handler must still degrade gracefully, not 500.
        var filters = new AiSearchFilters(null, null, null, "today", false);
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "ClassAttendance", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: [],
            ClampedFilters: filters, Unrestricted: false, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Teacher_asking_about_a_class_they_do_not_teach_is_clamped_by_the_real_authorization_service_and_returns_Unsupported()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        // Teacher is assigned only to "8A" via a real TimetableSlots row (same pattern as
        // AiSearchAuthorizationServiceTests.Teacher_querying_a_class_they_do_not_teach_...).
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

        // Real authorization: teacher only teaches "8A" but asks about "9B" — the service must
        // clamp ClassName/Section back to null (verified by AiSearchAuthorizationServiceTests).
        var auth = await Authorize(
            app, tenantId, teacherUserId, "ClassAttendance",
            new AiSearchFilters(null, "9B", null, "today", false), [Policies.Teacher]);

        auth.Allowed.Should().BeTrue();
        auth.ClampedFilters.ClassName.Should().BeNull();

        // Feed the REAL clamped result into the handler — this proves the full authorization
        // service -> handler pipeline degrades gracefully, not just that the handler reacts
        // correctly to a hand-crafted null.
        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task ForClassAsync_happy_path_returns_data_and_answer_with_matching_numbers()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        const string className = "8A";

        var student1 = Guid.NewGuid();
        var student2 = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                VALUES (@student1, @tenantId, @adm1, N'Class Present', N'8', N'A', @className, N'active')
                """,
                new { student1, tenantId, adm1 = $"ADM-CA1-{Guid.NewGuid():N}"[..20], className });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                VALUES (@student2, @tenantId, @adm2, N'Class Absent', N'8', N'A', @className, N'active')
                """,
                new { student2, tenantId, adm2 = $"ADM-CA2-{Guid.NewGuid():N}"[..20], className });

            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @student1, @date, 1, N'Math', N'present')
                """,
                new { tenantId, classId, student1, date = today.ToDateTime(TimeOnly.MinValue) });
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @student2, @date, 1, N'Math', N'absent')
                """,
                new { tenantId, classId, student2, date = today.ToDateTime(TimeOnly.MinValue) });
        });

        // Admin-like caller: Unrestricted, filters pass through unclamped (mirrors
        // AiSearchAuthorizationServiceTests.Admin_filters_pass_through_unclamped).
        var filters = new AiSearchFilters(null, className, null, "today", false);
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "ClassAttendance", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: null,
            ClampedFilters: filters, Unrestricted: true, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("ClassAttendance");
        response.Data.Should().NotBeNull();

        var data = response.Data!;
        var type = data.GetType();
        var total = (int)type.GetProperty("total")!.GetValue(data)!;
        var present = (int)type.GetProperty("present")!.GetValue(data)!;
        var absent = (int)type.GetProperty("absent")!.GetValue(data)!;
        var pct = (decimal)type.GetProperty("attendancePercentage")!.GetValue(data)!;

        total.Should().Be(2);
        present.Should().Be(1);
        absent.Should().Be(1);
        pct.Should().Be(50.00m);

        // The rendered answer must never diverge from the data field it was built from.
        response.Answer.Should().Contain(present.ToString());
        response.Answer.Should().Contain(total.ToString());
        response.Answer.Should().Contain(absent.ToString());
        response.Answer.Should().Contain(pct.ToString());
    }

    /// Finding 1: production Students.ClassLabel is generated as Grade + '-' + Section (e.g. "8-A"),
    /// but a caller's free-text class filter (or a TimetableSlots.ClassName value) is commonly the
    /// compact form "8A" with no dash. Before the fix, ForClassAsync's exact SQL equality on
    /// s.ClassLabel = @className meant this realistic mismatch always returned a zero/empty
    /// aggregate — this test proves the free-text filter now resolves to the real stored label and
    /// returns the actual data.
    [Fact]
    public async Task ForClassAsync_matches_a_compact_free_text_filter_against_a_hyphenated_ClassLabel()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        const string storedClassLabel = "8-A"; // realistic production shape: Grade + '-' + Section
        const string freeTextFilter = "8A"; // realistic caller/timetable phrasing: no dash

        var student1 = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                VALUES (@student1, @tenantId, @adm1, N'Compact Match', N'8', N'A', @storedClassLabel, N'active')
                """,
                new { student1, tenantId, adm1 = $"ADM-CAF-{Guid.NewGuid():N}"[..20], storedClassLabel });

            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @student1, @date, 1, N'Math', N'present')
                """,
                new { tenantId, classId, student1, date = today.ToDateTime(TimeOnly.MinValue) });
        });

        var filters = new AiSearchFilters(null, freeTextFilter, null, "today", false);
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "ClassAttendance", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: null,
            ClampedFilters: filters, Unrestricted: true, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("ClassAttendance");
        var data = response.Data!;
        var type = data.GetType();
        var total = (int)type.GetProperty("total")!.GetValue(data)!;
        var present = (int)type.GetProperty("present")!.GetValue(data)!;

        total.Should().Be(1, "the free-text filter \"8A\" must resolve to the real stored ClassLabel \"8-A\"");
        present.Should().Be(1);
    }
}
