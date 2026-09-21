using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Sis;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises StudentAttendanceHandler directly (no HTTP layer needed yet — the AI search controller
/// lands in Task 12). ResolvedStudentId is always re-derived by AiSearchAuthorizationService from
/// the caller's own identity/links, never from the raw LLM-extracted filter — a null here means "not
/// narrowed to one student" and must degrade to Unsupported rather than querying anything.
[Collection("sql")]
public class StudentAttendanceHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    /// Resolves a StudentAttendanceHandler wired to the real ISisService, with the ambient
    /// ITenantContext set exactly as the request pipeline would after JWT validation.
    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth, string language = "en")
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new StudentAttendanceHandler(
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

    private static AiAuthorizationResult AuthWith(Guid? studentId) => new(
        Allowed: true, ResultIntent: "StudentAttendance", ResolvedStudentId: studentId,
        AllowedChildStudentIds: null, AllowedClassNames: null,
        ClampedFilters: new AiSearchFilters(null, null, null, null, false),
        Unrestricted: false, NameUnmatched: false);

    [Fact]
    public async Task No_resolved_student_id_returns_no_match_rather_than_throwing()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();

        using var scope = app.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>();

        var response = await Handle(app, tenantId, AuthWith(null), "en");

        response.Intent.Should().Be("Unsupported");
        response.Answer.Should().Be(templates.RenderNoMatch("en"));
        response.Data.Should().BeNull();
    }

    [Fact]
    public async Task Resolved_student_returns_their_live_attendance_percentage()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 3 marked periods, 2 present/late -> 66.67% via the same LivePctSelect the handler reads.
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                VALUES (@studentId, @tenantId, @adm, N'Ravi Kumar', N'8', N'A', N'8A', N'active')
                """,
                new { studentId, tenantId, adm = $"ADM-SA1-{Guid.NewGuid():N}"[..20] });

            var date = today.ToDateTime(TimeOnly.MinValue);
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @studentId, @date, 1, N'Math', N'present')
                """,
                new { tenantId, classId, studentId, date });
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @studentId, @date, 2, N'English', N'late')
                """,
                new { tenantId, classId, studentId, date });
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @studentId, @date, 3, N'Science', N'absent')
                """,
                new { tenantId, classId, studentId, date });
        });

        var response = await Handle(app, tenantId, AuthWith(studentId), "en");

        response.Intent.Should().Be("StudentAttendance");
        response.Data.Should().NotBeNull();

        var data = response.Data!;
        var type = data.GetType();
        var returnedStudentId = (Guid)type.GetProperty("studentId")!.GetValue(data)!;
        var name = (string)type.GetProperty("name")!.GetValue(data)!;
        var pct = (decimal)type.GetProperty("attendancePercentage")!.GetValue(data)!;

        returnedStudentId.Should().Be(studentId);
        name.Should().Be("Ravi Kumar");
        pct.Should().Be(66.67m);

        // The rendered answer must never diverge from the data it was built from.
        response.Answer.Should().Contain(name);
        response.Answer.Should().Contain(pct.ToString());
    }
}
