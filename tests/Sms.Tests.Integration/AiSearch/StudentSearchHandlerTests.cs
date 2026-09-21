using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Sis;
using Sms.Modules.Sis.Contracts;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises StudentSearchHandler directly (no HTTP layer needed yet — the AI search controller
/// lands in a later task). StudentSearchHandler is the one handler that calls the unrestricted
/// ISisService.ListStudentsAsync, so the whole point of these tests is proving the gate is
/// auth.Unrestricted — never a null/emptiness check on AllowedChildStudentIds/AllowedClassNames,
/// which (per AiAuthorizationResult's docs) can legitimately be non-null/empty for a real
/// zero-or-scoped-record caller, not "no filter".
[Collection("sql")]
public class StudentSearchHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    /// Resolves a StudentSearchHandler wired to the real ISisService, with the ambient
    /// ITenantContext set exactly as the request pipeline would after JWT validation.
    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth, string language = "en",
        int page = 1, int pageSize = 20)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new StudentSearchHandler(
            scope.ServiceProvider.GetRequiredService<ISisService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        return await handler.HandleAsync(auth, language, page, pageSize);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task InsertStudent(
        NpgsqlConnection conn, Guid id, Guid tenantId, string name, string admissionNoPrefix) =>
        await conn.ExecuteAsync(
            """
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
            VALUES (@id, @tenantId, @adm, @name, N'8', N'A', N'8A', N'active')
            """,
            new { id, tenantId, adm = $"{admissionNoPrefix}-{Guid.NewGuid():N}"[..20], name });

    private static AiAuthorizationResult ParentScopedAuth(IReadOnlyList<Guid> childIds) => new(
        Allowed: true, ResultIntent: "StudentSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: childIds, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters("Aarav", null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    private static AiAuthorizationResult AdminAuth(string? studentName) => new(
        Allowed: true, ResultIntent: "StudentSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(studentName, null, null, null, false),
        Unrestricted: true, NameUnmatched: false);

    [Fact]
    public async Task Parent_scoped_auth_result_never_reaches_the_open_roster_search()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        // A parent auth result carries a real, non-null/non-empty AllowedChildStudentIds — the
        // "parent shape" per AiSearchAuthorizationService — but Unrestricted stays false. The handler
        // must gate on Unrestricted only and must never call the open ListStudentsAsync for this
        // caller, no matter what AllowedChildStudentIds/StudentName look like.
        var response = await Handle(app, tenantId, ParentScopedAuth([childId]));

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Teacher_scoped_auth_result_with_zero_scope_also_never_reaches_the_open_roster_search()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        // A teacher with no matching dbo.Teachers row gets an empty (not null) AllowedClassNames —
        // a real zero-scope answer per AiAuthorizationResult's docs, not "no filter". Must still
        // degrade to Unsupported rather than falling through to the unrestricted search.
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "StudentSearch", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: [],
            ClampedFilters: new AiSearchFilters("Aarav", null, null, null, false),
            Unrestricted: false, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Admin_search_returns_matching_students_for_their_own_tenant_only()
    {
        await using var app = App();
        var tenantIdA = Guid.NewGuid();
        var tenantIdB = Guid.NewGuid();

        await Seed(async conn =>
        {
            await InsertStudent(conn, Guid.NewGuid(), tenantIdA, "Aarav Shah", "ADM-SS1");
            await InsertStudent(conn, Guid.NewGuid(), tenantIdB, "Aarav Shah", "ADM-SS2");
        });

        var response = await Handle(app, tenantIdA, AdminAuth("Aarav"));

        response.Intent.Should().Be("StudentSearch");
        response.Data.Should().NotBeNull();

        var rows = (IReadOnlyList<StudentResponse>)response.Data!;
        rows.Should().ContainSingle();
        rows[0].TenantId.Should().Be(tenantIdA);
        rows[0].Name.Should().Be("Aarav Shah");
    }

    [Fact]
    public async Task Admin_search_with_no_matches_returns_no_match_answer_not_null_data_crash()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var response = await Handle(app, tenantId, AdminAuth("NoSuchStudentXyz"));

        response.Intent.Should().Be("StudentSearch");
        response.Data.Should().NotBeNull();
        var rows = (IReadOnlyList<StudentResponse>)response.Data!;
        rows.Should().BeEmpty();

        var templates = app.Services.GetRequiredService<IAiAnswerTemplateService>();
        response.Answer.Should().Be(templates.RenderNoMatch("en"));
    }

    /// Finding 3: ISisService.ListStudentsAsync is unpaged (returns every matching row, NextCursor
    /// always null), so the handler itself must slice by page/pageSize and compute a real
    /// hasNextPage — not echo back the requested page as if it were honored. Seeds more matching
    /// rows than pageSize and proves page 1 and page 2 return disjoint slices with the correct flag.
    [Fact]
    public async Task Result_set_larger_than_page_size_is_actually_sliced_with_a_correct_hasNextPage()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        var ids = new List<Guid>();
        await Seed(async conn =>
        {
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                await InsertStudent(conn, id, tenantId, $"PageTest Student {i}", $"ADM-PG{i}");
            }
        });

        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "StudentSearch", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: null,
            ClampedFilters: new AiSearchFilters("PageTest", null, null, null, false),
            Unrestricted: true, NameUnmatched: false);

        var page1 = await Handle(app, tenantId, auth, page: 1, pageSize: 2);
        var page2 = await Handle(app, tenantId, auth, page: 2, pageSize: 2);
        var page3 = await Handle(app, tenantId, auth, page: 3, pageSize: 2);

        var rows1 = (IReadOnlyList<StudentResponse>)page1.Data!;
        var rows2 = (IReadOnlyList<StudentResponse>)page2.Data!;
        var rows3 = (IReadOnlyList<StudentResponse>)page3.Data!;

        page1.Count.Should().Be(5, "Count reports the total match count, not the page size");
        rows1.Should().HaveCount(2);
        rows2.Should().HaveCount(2);
        rows3.Should().HaveCount(1, "5 rows at pageSize 2 leaves a single row on the last page");

        rows1.Select(r => r.Id).Should().NotIntersectWith(rows2.Select(r => r.Id),
            "page 2 must never silently repeat page 1's content mislabeled as the next page");

        page1.HasNextPage.Should().BeTrue();
        page2.HasNextPage.Should().BeTrue();
        page3.HasNextPage.Should().BeFalse();
    }
}

/// Exercises StudentDetailsHandler directly. The handler must rely solely on
/// AiAuthorizationResult.ResolvedStudentId — never throw, never leak — when that id is null, and
/// otherwise must return the real student resolved via ISisService.GetStudentAsync.
[Collection("sql")]
public class StudentDetailsHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth, string language = "en")
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new StudentDetailsHandler(
            scope.ServiceProvider.GetRequiredService<ISisService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        return await handler.HandleAsync(auth, language, 1, 20);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task InsertStudent(
        NpgsqlConnection conn, Guid id, Guid tenantId, string name, string admissionNoPrefix) =>
        await conn.ExecuteAsync(
            """
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
            VALUES (@id, @tenantId, @adm, @name, N'8', N'A', N'8A', N'active')
            """,
            new { id, tenantId, adm = $"{admissionNoPrefix}-{Guid.NewGuid():N}"[..20], name });

    private static AiAuthorizationResult AuthWithResolvedStudent(Guid? studentId) => new(
        Allowed: true, ResultIntent: "StudentDetails", ResolvedStudentId: studentId,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Null_resolved_student_id_never_reaches_GetStudentAsync_and_never_throws()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        // A caller whose ResolvedStudentId is null must degrade to Unsupported — this is the
        // security-relevant case: no id, no lookup, no leak, no throw.
        var response = await Handle(app, tenantId, AuthWithResolvedStudent(null));

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Resolved_student_id_returns_that_students_real_details()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        await Seed(async conn =>
            await InsertStudent(conn, studentId, tenantId, "Priya Nair", "ADM-SD1"));

        var response = await Handle(app, tenantId, AuthWithResolvedStudent(studentId));

        response.Intent.Should().Be("StudentDetails");
        response.Data.Should().NotBeNull();
        var student = (StudentResponse)response.Data!;
        student.Id.Should().Be(studentId);
        student.Name.Should().Be("Priya Nair");
        response.Answer.Should().Be("Showing details for Priya Nair.");
    }
}
