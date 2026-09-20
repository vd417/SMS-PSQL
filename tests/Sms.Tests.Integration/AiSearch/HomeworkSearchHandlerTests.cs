using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Academics.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises HomeworkSearchHandler directly (no HTTP layer needed yet). Covers the single-student
/// clamp — a handler gated purely on AiAuthorizationResult.ResolvedStudentId, per
/// AiSearchAuthorizationService (self-referential or single-name-matched-parent queries only) — and
/// the "no resolved student" Unsupported fallback for e.g. a teacher's generic "homework" ask.
[Collection("sql")]
public class HomeworkSearchHandlerTests(PostgresFixture fx)
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
        var handler = new HomeworkSearchHandler(
            scope.ServiceProvider.GetRequiredService<HomeworkRepository>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    private async Task Seed(Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task InsertHomework(
        SqlConnection conn, Guid id, Guid tenantId, Guid studentId, string title) =>
        await conn.ExecuteAsync(
            """
            INSERT dbo.Homework (Id, TenantId, StudentId, Title, Status, Priority)
            VALUES (@id, @tenantId, @studentId, @title, N'todo', N'med')
            """,
            new { id, tenantId, studentId, title });

    private static AiAuthorizationResult ResolvedStudentAuth(Guid studentId) => new(
        Allowed: true, ResultIntent: "HomeworkSearch", ResolvedStudentId: studentId,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    private static AiAuthorizationResult NoResolvedStudentAuth() => new(
        Allowed: true, ResultIntent: "HomeworkSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Only_the_resolved_students_homework_is_returned()
    {
        var tenantId = Guid.NewGuid();
        var studentA = Guid.NewGuid();
        var studentB = Guid.NewGuid();

        await Seed(async conn =>
        {
            await InsertHomework(conn, Guid.NewGuid(), tenantId, studentA, "Math worksheet");
            await InsertHomework(conn, Guid.NewGuid(), tenantId, studentA, "Science project");
            await InsertHomework(conn, Guid.NewGuid(), tenantId, studentB, "Other student's essay");
        });

        await using var app = App();
        var response = await Handle(app, tenantId, ResolvedStudentAuth(studentA));

        response.Intent.Should().Be("HomeworkSearch");
        response.Count.Should().Be(2);
        var rows = (List<HomeworkResponse>)response.Data!;
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.StudentId == studentA);
        rows.Should().NotContain(r => r.Title == "Other student's essay");
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
