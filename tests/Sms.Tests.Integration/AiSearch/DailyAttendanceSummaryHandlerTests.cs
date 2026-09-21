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
using Sms.Modules.AiSearch.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Resolves AiAttendanceAggregateRepository directly (no HTTP layer needed yet — the AI search
/// controller lands in Task 12) and asserts SchoolWideAsync only counts the authenticated
/// tenant's active students marked today, never another tenant's rows.
[Collection("sql")]
public class DailyAttendanceSummaryHandlerTests(PostgresFixture fx)
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
        var jwt = new Sms.Shared.Kernel.Auth.JwtTokenService(
            new Sms.Shared.Kernel.Auth.JwtOptions
            {
                Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15,
            },
            new Sms.Shared.Kernel.Time.SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
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
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task MarkPresent(
        NpgsqlConnection conn, Guid tenantId, Guid studentId, DateOnly date, string status)
    {
        await conn.ExecuteAsync(
            """
            INSERT dbo.PeriodAttendanceRecords
                (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
            VALUES
                (NEWID(), @tenantId, @classId, @studentId, @date, 1, N'Math', @status)
            """,
            new { tenantId, classId = Guid.NewGuid(), studentId, date = date.ToDateTime(TimeOnly.MinValue), status });
    }

    /// Runs <paramref name="act"/> against a scope whose ambient ITenantContext matches the tenant
    /// being queried, exactly as the request pipeline would have set it after JWT validation.
    private static async Task<AttendanceAggregate> AsTenant(
        WebApplicationFactory<Program> app, Guid tenantId,
        Func<AiAttendanceAggregateRepository, Task<AttendanceAggregate>> act)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        return await act(scope.ServiceProvider.GetRequiredService<AiAttendanceAggregateRepository>());
    }

    [Fact]
    public async Task Aggregate_counts_only_the_authenticated_tenants_active_students()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var adminA = Admin(app, tenantA);
        var adminB = Admin(app, tenantB);

        // Tenant A: 2 active students, one present, one absent today.
        var a1 = await Data(await adminA.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-A1-{Guid.NewGuid():N}"[..20],
            name = "Tenant A Present",
            grade = "IV",
            section = "B",
            roll = 1,
        }), HttpStatusCode.Created);
        var a2 = await Data(await adminA.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-A2-{Guid.NewGuid():N}"[..20],
            name = "Tenant A Absent",
            grade = "IV",
            section = "B",
            roll = 2,
        }), HttpStatusCode.Created);

        // Tenant B: 1 active student, present today — must never be counted for tenant A.
        var b1 = await Data(await adminB.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-B1-{Guid.NewGuid():N}"[..20],
            name = "Tenant B Present",
            grade = "IV",
            section = "B",
            roll = 1,
        }), HttpStatusCode.Created);

        await Seed(async conn =>
        {
            await MarkPresent(conn, tenantA, a1.GetProperty("id").GetGuid(), today, "present");
            await MarkPresent(conn, tenantA, a2.GetProperty("id").GetGuid(), today, "absent");
            await MarkPresent(conn, tenantB, b1.GetProperty("id").GetGuid(), today, "present");
        });

        var aggA = await AsTenant(app, tenantA, repo => repo.SchoolWideAsync(tenantA, today));

        aggA.Total.Should().Be(2);
        aggA.Present.Should().Be(1);
        aggA.Absent.Should().Be(1);
        aggA.Pct.Should().Be(50.00m);

        var aggB = await AsTenant(app, tenantB, repo => repo.SchoolWideAsync(tenantB, today));

        aggB.Total.Should().Be(1);
        aggB.Present.Should().Be(1);
        aggB.Absent.Should().Be(0);
        aggB.Pct.Should().Be(100.00m);
    }

    /// Resolves a DailyAttendanceSummaryHandler wired to the real repository, with the ambient
    /// ITenantContext set exactly as the request pipeline would after JWT validation.
    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, AiAuthorizationResult auth)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, Guid.NewGuid(), isPlatform: false);
        var handler = new DailyAttendanceSummaryHandler(
            scope.ServiceProvider.GetRequiredService<AiAttendanceAggregateRepository>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    /// Mirrors ClassAttendanceHandlerTests.ForClassAsync_matches_a_compact_free_text_filter_against_a_hyphenated_ClassLabel:
    /// production Students.ClassLabel is generated as Grade + '-' + Section (e.g. "8-A"), but a
    /// teacher's AllowedClassNames[0] comes from the free-text dbo.Classes.Name column (e.g. "8A").
    /// Before this fix, DailyAttendanceSummaryHandler passed that free-text name straight into
    /// ForClassAsync's exact SQL equality on s.ClassLabel, so this realistic mismatch always
    /// returned a zero/empty aggregate for a teacher's own daily summary. This test proves the
    /// free-text class name now resolves to the real stored label and returns the actual data.
    [Fact]
    public async Task Teacher_scoped_summary_matches_a_compact_free_text_class_name_against_a_hyphenated_ClassLabel()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        const string storedClassLabel = "8-A"; // realistic production shape: Grade + '-' + Section
        const string freeTextClassName = "8A"; // realistic dbo.Classes.Name phrasing: no dash

        var student1 = Guid.NewGuid();

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
                VALUES (@student1, @tenantId, @adm1, N'Daily Compact Match', N'8', N'A', @storedClassLabel, N'active')
                """,
                new { student1, tenantId, adm1 = $"ADM-DAF-{Guid.NewGuid():N}"[..20], storedClassLabel });

            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                """
                INSERT dbo.PeriodAttendanceRecords (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status)
                VALUES (NEWID(), @tenantId, @classId, @student1, @date, 1, N'Math', N'present')
                """,
                new { tenantId, classId, student1, date = today.ToDateTime(TimeOnly.MinValue) });
        });

        // Teacher (never Unrestricted), clamped to their own class via the free-text Classes.Name.
        var filters = new AiSearchFilters(null, null, null, "today", false);
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "DailyAttendanceSummary", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: [freeTextClassName],
            ClampedFilters: filters, Unrestricted: false, NameUnmatched: false);

        var response = await Handle(app, tenantId, auth);

        response.Intent.Should().Be("DailyAttendanceSummary");
        response.Data.Should().NotBeNull();

        var data = response.Data!;
        var type = data.GetType();
        var total = (int)type.GetProperty("totalStudents")!.GetValue(data)!;
        var present = (int)type.GetProperty("present")!.GetValue(data)!;

        total.Should().Be(1, "the free-text class name \"8A\" must resolve to the real stored ClassLabel \"8-A\"");
        present.Should().Be(1);
    }
}
