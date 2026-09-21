using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.Academics;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Attendance;

[Collection("sql")]
public sealed class PeriodAttendanceAdvancedSummaryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    // Seeded attendance date must stay comfortably inside the "last_30_days" preset (today-29..today)
    // no matter when CI runs, and must land on a Wednesday to match the seeded TimetableSlots row.
    // A fixed calendar date drifts out of that rolling window as real time passes — this recomputes
    // a same-weekday date ~2 weeks back every run instead.
    private static readonly DateTime SeedDate = MostRecentWednesdayOnOrBefore(DateTime.UtcNow.Date.AddDays(-14));
    private static readonly string Day = SeedDate.ToString("yyyy-MM-dd");

    private static DateTime MostRecentWednesdayOnOrBefore(DateTime from)
    {
        var offset = ((int)from.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return from.AddDays(-offset);
    }

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Production");
            builder.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            builder.UseSetting("Jwt:SigningKey", Key);
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IAttendanceViewPermissionService, AttendanceViewPermissionService>();
            });
        });

    [Fact]
    public async Task Class_summary_returns_the_class_day_rollup()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.AdminUserId, seed.TenantId, "admin");

        var data = await Data(
            await client.GetAsync(
                $"/v1/attendance/period-records/summary/class?classId={seed.ClassAId}&date={Day}"));

        data.GetProperty("total_students").GetInt32().Should().Be(1);
        data.GetProperty("present").GetInt32().Should().Be(1);
        data.GetProperty("total_periods").GetInt32().Should().Be(1);
        data.GetProperty("marked_periods").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Subjects_summary_returns_rows_for_the_requested_range()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.AdminUserId, seed.TenantId, "admin");

        var data = await Data(
            await client.GetAsync(
                $"/v1/attendance/period-records/summary/subjects?classId={seed.ClassAId}&preset=last_30_days"));

        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("subject").GetString().Should().Be("Math");
        data[0].GetProperty("marked").GetInt32().Should().Be(1);
        data[0].GetProperty("present").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Teachers_summary_returns_teacher_compliance_rows()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.AdminUserId, seed.TenantId, "admin");

        var data = await Data(
            await client.GetAsync(
                "/v1/attendance/period-records/summary/teachers?preset=last_30_days"));

        data.EnumerateArray().Should().Contain(row =>
            row.GetProperty("teacher_id").GetGuid() == seed.TeacherAId
            && row.GetProperty("marked_periods").GetInt32() == 1);
        data.EnumerateArray().Should().Contain(row =>
            row.GetProperty("teacher_id").GetGuid() == seed.TeacherBId);
    }

    [Fact]
    public async Task Range_summary_applies_the_requested_filters()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.AdminUserId, seed.TenantId, "admin");

        var data = await Data(
            await client.GetAsync(
                $"/v1/attendance/period-records/summary/range?preset=last_30_days&classId={seed.ClassAId}"));

        data.GetProperty("total_marked_periods").GetInt32().Should().Be(1);
        data.GetProperty("present").GetInt32().Should().Be(1);
        data.GetProperty("absent").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Scoped_teacher_cannot_read_another_class_or_teacher_rollup()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.TeacherAUserId, seed.TenantId, Policies.Teacher);

        var forbidden = await client.GetAsync(
            $"/v1/attendance/period-records/summary/class?classId={seed.ClassBId}&date={Day}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var subjectsForbidden = await client.GetAsync(
            $"/v1/attendance/period-records/summary/subjects?classId={seed.ClassBId}&from={Day}&to={Day}");
        subjectsForbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var teachers = await Data(
            await client.GetAsync(
                $"/v1/attendance/period-records/summary/teachers?from={Day}&to={Day}"));
        teachers.GetArrayLength().Should().Be(1);
        teachers[0].GetProperty("teacher_id").GetGuid().Should().Be(seed.TeacherAId);

        var range = await Data(
            await client.GetAsync(
                $"/v1/attendance/period-records/summary/range?from={Day}&to={Day}&teacherId={seed.TeacherBId}"));
        range.GetProperty("present").GetInt32().Should().Be(1);
        range.GetProperty("absent").GetInt32().Should().Be(0);
    }

    private static HttpClient Client(
        WebApplicationFactory<Program> app,
        Guid userId,
        Guid tenantId,
        string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions
            {
                Issuer = "sms",
                Audience = "sms-apps",
                SigningKey = Key,
                AccessTokenMinutes = 15,
            },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task<SummarySeed> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var teacherAUserId = Guid.NewGuid();
        var teacherBUserId = Guid.NewGuid();
        var teacherAId = Guid.NewGuid();
        var teacherBId = Guid.NewGuid();
        var classAId = Guid.NewGuid();
        var classBId = Guid.NewGuid();
        var studentAId = Guid.NewGuid();
        var studentBId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using var connection = new NpgsqlConnection(fx.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId",
            new { tenantId });
        await connection.ExecuteAsync(
            """
            INSERT dbo.Users (Id, TenantId) VALUES
                (@adminUserId, @tenantId),
                (@teacherAUserId, @tenantId),
                (@teacherBUserId, @tenantId);
            INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES
                (@teacherAId, @tenantId, N'Teacher A', @teacherAUserId),
                (@teacherBId, @tenantId, N'Teacher B', @teacherBUserId);
            INSERT dbo.Classes (Id, TenantId, Name, Grade, Section, StudentCount, ClassTeacherId) VALUES
                (@classAId, @tenantId, N'IX-A', N'IX', N'A', 1, @teacherAId),
                (@classBId, @tenantId, N'IX-B', N'IX', N'B', 1, @teacherBId);
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, Status) VALUES
                (@studentAId, @tenantId, N'SUM-A', N'Student A', N'IX', N'A', N'active'),
                (@studentBId, @tenantId, N'SUM-B', N'Student B', N'IX', N'B', N'active');
            INSERT dbo.TimetableSlots
                (TenantId, [Day], Period, Subject, ClassId, ClassName, StartTime, EndTime, TeacherId)
            VALUES
                (@tenantId, N'Wed', 1, N'Math', @classAId, N'IX-A', N'09:00', N'09:45', @teacherAId),
                (@tenantId, N'Wed', 1, N'Science', @classBId, N'IX-B', N'09:00', N'09:45', @teacherBId);
            INSERT dbo.PeriodAttendanceRecords
                (TenantId, ClassId, StudentId, [Date], Period, Subject, Status, MarkedBy, MarkedByRole)
            VALUES
                (@tenantId, @classAId, @studentAId, @date, 1, N'Math', N'present', @teacherAUserId, N'teacher'),
                (@tenantId, @classBId, @studentBId, @date, 1, N'Science', N'absent', @teacherBUserId, N'teacher');
            """,
            new
            {
                tenantId,
                adminUserId,
                teacherAUserId,
                teacherBUserId,
                teacherAId,
                teacherBId,
                classAId,
                classBId,
                studentAId,
                studentBId,
                date = SeedDate,
            });

        return new SummarySeed(
            tenantId,
            adminUserId,
            teacherAUserId,
            teacherAId,
            teacherBId,
            classAId,
            classBId);
    }

    private sealed record SummarySeed(
        Guid TenantId,
        Guid AdminUserId,
        Guid TeacherAUserId,
        Guid TeacherAId,
        Guid TeacherBId,
        Guid ClassAId,
        Guid ClassBId);
}
