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
using Sms.Modules.Staffing.Data;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises PersonLookupHandler through the REAL AiSearchAuthorizationService.AuthorizeAsync
/// pipeline, mirroring the seeding/DI conventions established in GreetByIdHandlerTests.cs and
/// PersonResolverTests.cs.
[Collection("sql")]
public class PersonLookupHandlerTests(PostgresFixture fx)
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

    private static async Task<Guid> InsertStudent(
        NpgsqlConnection conn, Guid tenantId, string name, string grade, string section, string classLabel)
    {
        var id = Guid.NewGuid();
        var admissionNo = $"ADM-{Guid.NewGuid():N}"[..20];
        await conn.ExecuteAsync(
            """
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
            VALUES (@id, @tenantId, @admissionNo, @name, @grade, @section, @classLabel, N'active')
            """,
            new { id, tenantId, admissionNo, name, grade, section, classLabel });
        return id;
    }

    private static async Task<Guid> InsertTeacher(
        NpgsqlConnection conn, Guid tenantId, string name, string subjectsCsv)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Teachers (Id, TenantId, Name, SubjectsCsv) VALUES (@id, @tenantId, @name, @subjectsCsv)",
            new { id, tenantId, name, subjectsCsv });
        return id;
    }

    /// Runs the REAL AiSearchAuthorizationService.AuthorizeAsync against the ambient tenant/user.
    private static async Task<AiAuthorizationResult> Authorize(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string name, string[] roles)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var svc = scope.ServiceProvider.GetRequiredService<IAiSearchAuthorizationService>();
        var filters = new AiSearchFilters(name, null, null, null, false);
        return await svc.AuthorizeAsync(Intent, filters, roles);
    }

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, AiAuthorizationResult auth,
        string language = "en", int page = 1, int pageSize = 20)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, userId, isPlatform: false);
        var handler = new PersonLookupHandler(
            scope.ServiceProvider.GetRequiredService<IPersonResolver>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>(),
            scope.ServiceProvider.GetRequiredService<TeacherRepository>(),
            scope.ServiceProvider.GetRequiredService<StaffRepository>(),
            scope.ServiceProvider.GetRequiredService<Sms.Application.Services.Sis.ISisService>(),
            scope.ServiceProvider.GetRequiredService<Sms.Shared.Kernel.Auth.IUserDirectoryLookup>());
        return await handler.HandleAsync(auth, language, page, pageSize);
    }

    /// End-to-end: Authorize (real) then Handle (real), for a caller with the given roles.
    private static async Task<AiSearchResponse> AuthorizeAndHandle(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string name, string[] roles,
        string language = "en", int page = 1, int pageSize = 20)
    {
        var auth = await Authorize(app, tenantId, userId, name, roles);
        auth.Allowed.Should().BeTrue();
        return await Handle(app, tenantId, userId, auth, language, page, pageSize);
    }

    [Fact]
    public async Task Single_teacher_match_renders_role_and_subjects_and_sets_ConversationUpdate()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        Guid teacherId = default;

        await SeedInTenant(tenantId, async conn =>
        {
            teacherId = await InsertTeacher(conn, tenantId, "Rahul Sharma", "Mathematics");
        });

        var callerId = Guid.NewGuid();
        var response = await AuthorizeAndHandle(app, tenantId, callerId, "Rahul", [Policies.SchoolAdmin]);

        response.Status.Should().Be("success");
        response.Answer.Should().Contain("Teacher");
        response.Answer.Should().Contain("Mathematics");
        response.ConversationUpdate.Should().NotBeNull();
        response.ConversationUpdate!.ResolvedEntityType.Should().Be("teacher");
        response.ConversationUpdate.ResolvedEntityId.Should().Be(teacherId);
    }

    [Fact]
    public async Task Single_student_match_renders_student_shape()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        await SeedInTenant(tenantId, async conn =>
        {
            await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
        });

        var callerId = Guid.NewGuid();
        var response = await AuthorizeAndHandle(app, tenantId, callerId, "Rahul", [Policies.SchoolAdmin]);

        response.Status.Should().Be("success");
        response.Answer.Should().Contain("Student");
        response.Answer.Should().Contain("8-A");
    }

    [Fact]
    public async Task Zero_matches_returns_no_match_status_with_Unsupported_intent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var callerId = Guid.NewGuid();

        var response = await AuthorizeAndHandle(app, tenantId, callerId, "Rahul", [Policies.SchoolAdmin]);

        response.Status.Should().Be("no_match");
        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
        response.ConversationUpdate.Should().BeNull();
    }

    [Fact]
    public async Task Multiple_matches_returns_needs_clarification_with_safe_fields_only_and_sets_PendingCandidates()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        Guid teacherId = default, studentId = default;

        await SeedInTenant(tenantId, async conn =>
        {
            teacherId = await InsertTeacher(conn, tenantId, "Rahul Sharma", "Mathematics");
            studentId = await InsertStudent(conn, tenantId, "Rahul Verma", "8", "A", "8-A");
        });

        var callerId = Guid.NewGuid();
        var response = await AuthorizeAndHandle(app, tenantId, callerId, "Rahul", [Policies.SchoolAdmin]);

        response.Status.Should().Be("needs_clarification");

        var json = JsonSerializer.Serialize(response.Data);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();
        items.Should().HaveCount(2);
        foreach (var item in items)
        {
            item.EnumerateObject().Select(p => p.Name.ToLowerInvariant())
                .Should().BeEquivalentTo(["name", "type", "detail"]);
        }
        json.Should().NotContain(teacherId.ToString());
        json.Should().NotContain(studentId.ToString());

        response.ConversationUpdate.Should().NotBeNull();
        var pending = response.ConversationUpdate!.PendingCandidates;
        pending.Should().NotBeNull();
        pending!.Should().HaveCount(2);
        pending.Should().Contain(p => p.Id == teacherId && p.Type == "teacher");
        pending.Should().Contain(p => p.Id == studentId && p.Type == "student");
    }

    /// Task 12: the direct pre-resolved short-circuit -- no name in ClampedFilters at all, just a
    /// PreResolvedEntityId/Type as AiSearchService would set after re-authorizing a conversation
    /// follow-up. The handler must skip PersonResolver's name search entirely and re-fetch this exact
    /// teacher's CURRENT name/subjects.
    [Fact]
    public async Task A_preresolved_teacher_renders_correctly_with_no_name_in_ClampedFilters()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        Guid teacherId = default;

        await SeedInTenant(tenantId, async conn =>
        {
            teacherId = await InsertTeacher(conn, tenantId, "Rahul Sharma", "Mathematics");
        });

        var callerId = Guid.NewGuid();
        var auth = new AiAuthorizationResult(
            true, Intent, null, null, null, new AiSearchFilters(null, null, null, null, false),
            Unrestricted: true, NameUnmatched: false,
            PreResolvedEntityId: teacherId, PreResolvedEntityType: "teacher");

        var response = await Handle(app, tenantId, callerId, auth);

        response.Status.Should().Be("success");
        response.Answer.Should().Contain("Mathematics");
        response.ConversationUpdate.Should().NotBeNull();
        response.ConversationUpdate!.ResolvedEntityId.Should().Be(teacherId);
        response.ConversationUpdate.ResolvedEntityType.Should().Be("teacher");
    }
}
