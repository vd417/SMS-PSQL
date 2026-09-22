using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Modules.Academics.Contracts;
using Sms.Modules.Academics.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises ExamRepository.ListUpcomingExamsAsync and UpcomingExamSearchHandler directly (no HTTP
/// layer needed yet). Covers both the repository's tenant + future-date filter, and the handler's
/// class-scoping discipline for non-Unrestricted callers (teacher/parent), mirroring
/// ClassAttendanceHandlerTests / DailyAttendanceSummaryHandlerTests.
[Collection("sql")]
public class UpcomingExamSearchHandlerTests(PostgresFixture fx)
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
        var handler = new UpcomingExamSearchHandler(
            scope.ServiceProvider.GetRequiredService<ExamRepository>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await work(conn);
    }

    private static async Task InsertExam(
        NpgsqlConnection conn, Guid id, Guid tenantId, string name, string? grades, DateTime? fromDate) =>
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Exams" ("Id", "TenantId", "Name", "Type", "Grades", "FromDate", "ToDate", "SubjectCount", "Status", "MarksEnteredPct", "Published")
            VALUES (@id, @tenantId, @name, 'term', @grades, @fromDate, @fromDate, 0, 'draft', 0, false)
            """,
            new { id, tenantId, name, grades, fromDate });

    private static AiAuthorizationResult UnrestrictedAuth() => new(
        Allowed: true, ResultIntent: "UpcomingExamSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: true, NameUnmatched: false);

    private static AiAuthorizationResult ClassClampedAuth(IReadOnlyList<string>? allowedClassNames) => new(
        Allowed: true, ResultIntent: "UpcomingExamSearch", ResolvedStudentId: null,
        AllowedChildStudentIds: null, AllowedClassNames: allowedClassNames,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task Repository_returns_only_the_callers_tenant_future_dated_exams()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        var pastExamA = Guid.NewGuid();
        var futureExamA = Guid.NewGuid();
        var futureExamB = Guid.NewGuid();

        await Seed(async conn =>
        {
            await InsertExam(conn, pastExamA, tenantA, "Past Midterm", "8", today.AddDays(-10));
            await InsertExam(conn, futureExamA, tenantA, "Future Finals", "8", today.AddDays(10));
            await InsertExam(conn, futureExamB, tenantB, "Other Tenant Finals", "8", today.AddDays(10));
        });

        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantA, Guid.NewGuid(), isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<ExamRepository>();

        var rows = await repo.ListUpcomingExamsAsync(tenantA, today);

        rows.Should().ContainSingle();
        rows[0].Id.Should().Be(futureExamA);
        rows[0].Name.Should().Be("Future Finals");
    }

    [Fact]
    public async Task Unrestricted_caller_sees_all_upcoming_exams_for_the_tenant()
    {
        var tenantId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await Seed(async conn =>
        {
            await InsertExam(conn, Guid.NewGuid(), tenantId, "8A Finals", "8", today.AddDays(5));
            await InsertExam(conn, Guid.NewGuid(), tenantId, "9B Finals", "9", today.AddDays(5));
        });

        await using var app = App();
        var response = await Handle(app, tenantId, UnrestrictedAuth());

        response.Intent.Should().Be("UpcomingExamSearch");
        response.Count.Should().Be(2);
    }

    [Fact]
    public async Task Non_unrestricted_caller_with_empty_allowed_classes_sees_zero_exams()
    {
        var tenantId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await Seed(async conn =>
        {
            await InsertExam(conn, Guid.NewGuid(), tenantId, "8A Finals", "8", today.AddDays(5));
        });

        await using var app = App();

        // AllowedClassNames = [] means "authorized for zero classes", not "no filter" — must never
        // fall through to the admin/unrestricted path and show the tenant's exam anyway.
        var response = await Handle(app, tenantId, ClassClampedAuth([]));

        response.Intent.Should().Be("UpcomingExamSearch");
        response.Count.Should().Be(0);
        var rows = (List<ExamResponse>)response.Data!;
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_unrestricted_caller_with_null_allowed_classes_sees_zero_exams()
    {
        var tenantId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await Seed(async conn =>
        {
            await InsertExam(conn, Guid.NewGuid(), tenantId, "8A Finals", "8", today.AddDays(5));
        });

        await using var app = App();

        // Same as the empty-list case: a non-Unrestricted caller with a null AllowedClassNames is
        // still "authorized for zero classes", never "unfiltered".
        var response = await Handle(app, tenantId, ClassClampedAuth(null));

        response.Intent.Should().Be("UpcomingExamSearch");
        response.Count.Should().Be(0);
    }

    [Fact]
    public async Task Non_unrestricted_caller_only_sees_exams_matching_their_allowed_classes()
    {
        var tenantId = Guid.NewGuid();
        var today = DateTime.UtcNow.Date;

        await Seed(async conn =>
        {
            await InsertExam(conn, Guid.NewGuid(), tenantId, "8A Finals", "8", today.AddDays(5));
            await InsertExam(conn, Guid.NewGuid(), tenantId, "9B Finals", "9", today.AddDays(5));
        });

        await using var app = App();
        var response = await Handle(app, tenantId, ClassClampedAuth(["8"]));

        response.Intent.Should().Be("UpcomingExamSearch");
        response.Count.Should().Be(1);
        var rows = (List<ExamResponse>)response.Data!;
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("8A Finals");
    }
}
