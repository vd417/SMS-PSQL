using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.Academics;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Sis;
using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises SubjectSearchHandler directly (no HTTP layer needed yet). Covers the single-student
/// clamp — a handler gated purely on AiAuthorizationResult.ResolvedStudentId, per
/// AiSearchAuthorizationService (self-referential or single-name-matched-parent queries only) — and
/// verifies subjects are scoped to the resolved student's own class, not leaked from another class.
[Collection("sql")]
public class SubjectSearchHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new SubjectSearchHandler(
            scope.ServiceProvider.GetRequiredService<IAcademicsService>(),
            scope.ServiceProvider.GetRequiredService<ISisService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static AiAuthorizationResult ResolvedStudentAuth(Guid studentId) => new(
        Allowed: true, ResultIntent: "SubjectSearch", ResolvedStudentId: studentId,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    private static AiAuthorizationResult NoResolvedStudentAuth() => new(
        Allowed: true, ResultIntent: "SubjectSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Subjects_are_scoped_to_the_resolved_students_class()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classAId = Guid.NewGuid();
        var classBId = Guid.NewGuid();

        await Seed(async conn =>
        {
            // Student belongs to class 9-A, whose homeroom subject is Mathematics.
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status) " +
                "VALUES (@studentId, @tenantId, @adm, N'Ankit', N'9', N'A', N'9-A', N'active')",
                new { studentId, tenantId, adm = $"ADM-SS1-{Guid.NewGuid():N}"[..20] });

            await conn.ExecuteAsync(
                "INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, Subject) " +
                "VALUES (@classAId, @tenantId, N'9-A', N'9', N'A', N'Mathematics'), " +
                "(@classBId, @tenantId, N'9-B', N'9', N'B', N'History')",
                new { classAId, classBId, tenantId });

            await conn.ExecuteAsync(
                "INSERT dbo.Subjects (Id, TenantId, Name, Short) VALUES " +
                "(NEWID(), @tenantId, N'Mathematics', N'Math'), " +
                "(NEWID(), @tenantId, N'Science', N'Sci'), " +
                "(NEWID(), @tenantId, N'History', N'Hist')",
                new { tenantId });

            // A Science timetable slot for the student's own class (9-A) and a History slot for
            // the other class (9-B) — only the 9-A subjects must come back.
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (Id, TenantId, [Day], Period, Subject, ClassId, ClassName) " +
                "VALUES (NEWID(), @tenantId, N'Mon', 1, N'Science', @classAId, N'9-A'), " +
                "(NEWID(), @tenantId, N'Mon', 1, N'History', @classBId, N'9-B')",
                new { tenantId, classAId, classBId });
        });

        await using var app = App();
        var response = await Handle(app, tenantId, ResolvedStudentAuth(studentId));

        response.Intent.Should().Be("SubjectSearch");
        var rows = (IReadOnlyList<SubjectResponse>)response.Data!;
        var names = rows.Select(r => r.Name).ToList();
        names.Should().BeEquivalentTo("Mathematics", "Science");
        names.Should().NotContain("History");
        response.Count.Should().Be(2);
    }

    [Fact]
    public async Task No_resolved_student_returns_Unsupported()
    {
        var tenantId = Guid.NewGuid();

        await using var app = App();
        var response = await Handle(app, tenantId, NoResolvedStudentAuth());

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();
    }
}
