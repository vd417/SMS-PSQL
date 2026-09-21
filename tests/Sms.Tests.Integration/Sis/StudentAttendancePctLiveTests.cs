using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Sis;

[Collection("sql")]
public class StudentAttendancePctLiveTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Student_attendance_pct_uses_period_records_not_daily()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                """INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Status") VALUES (@studentId, @tenantId, 'A1', 'S1', 'active')""",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                """INSERT INTO "dbo"."Classes" ("Id", "TenantId", "Name", "StudentCount") VALUES (@classId, @tenantId, 'C1', 0)""",
                new { classId, tenantId });

            // Legacy daily: would be 25% if used (1 present / 4 days) — must be ignored
            var daily = new[] { "present", "absent", "absent", "absent" };
            for (int i = 0; i < daily.Length; i++)
                await conn.ExecuteAsync(
                    """INSERT INTO "dbo"."AttendanceRecords" ("TenantId", "ClassId", "StudentId", "Date", "Status") VALUES (@tenantId, @classId, @studentId, @date, @status)""",
                    new { tenantId, classId, studentId, date = DateTime.UtcNow.Date.AddDays(-i), status = daily[i] });

            // Period: 8 present + 1 late + 2 absent = 81.82%
            var periods = new (string Status, int Period)[]
            {
                ("present", 1), ("present", 2), ("present", 3), ("present", 4),
                ("present", 5), ("present", 6), ("present", 7), ("present", 8),
                ("late", 9), ("absent", 10), ("absent", 11),
            };
            foreach (var (status, period) in periods)
                await conn.ExecuteAsync("""
                    INSERT INTO "dbo"."PeriodAttendanceRecords"
                      ("Id", "TenantId", "ClassId", "StudentId", "Date", "Period", "Subject", "Status", "CreatedAt", "UpdatedAt")
                    VALUES
                      (gen_random_uuid(), @tenantId, @classId, @studentId, @date, @period, 'Math', @status, now(), now())
                    """,
                    new { tenantId, classId, studentId, date = DateTime.UtcNow.Date, period, status });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/students/{studentId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("attendance_pct").GetDecimal().Should().Be(81.82m);

        var list = await client.GetAsync("/v1/students");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var listed = listDoc.RootElement.GetProperty("data").EnumerateArray().First(e => e.GetProperty("id").GetGuid() == studentId);
        listed.GetProperty("attendance_pct").GetDecimal().Should().Be(81.82m);

        var summary = await client.GetAsync($"/v1/students/{studentId}/attendance/summary");
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sumDoc = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        var data = sumDoc.RootElement.GetProperty("data");
        data.GetProperty("total_marked_periods").GetInt32().Should().Be(11);
        data.GetProperty("present_periods").GetInt32().Should().Be(8);
        data.GetProperty("late_periods").GetInt32().Should().Be(1);
        data.GetProperty("absent_periods").GetInt32().Should().Be(2);
        data.GetProperty("attendance_percentage").GetDecimal().Should().Be(81.82m);
    }

    [Fact]
    public async Task Student_attendance_pct_null_when_only_legacy_daily_exists()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var classId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                """INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Status") VALUES (@studentId, @tenantId, 'A2', 'S2', 'active')""",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                """INSERT INTO "dbo"."Classes" ("Id", "TenantId", "Name", "StudentCount") VALUES (@classId, @tenantId, 'C2', 0)""",
                new { classId, tenantId });
            await conn.ExecuteAsync(
                """INSERT INTO "dbo"."AttendanceRecords" ("TenantId", "ClassId", "StudentId", "Date", "Status") VALUES (@tenantId, @classId, @studentId, @date, 'present')""",
                new { tenantId, classId, studentId, date = DateTime.UtcNow.Date });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/students/{studentId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("attendance_pct").ValueKind.Should().Be(JsonValueKind.Null);

        var summary = await client.GetAsync($"/v1/students/{studentId}/attendance/summary");
        using var sumDoc = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        sumDoc.RootElement.GetProperty("data").GetProperty("attendance_percentage").ValueKind.Should().Be(JsonValueKind.Null);
        sumDoc.RootElement.GetProperty("data").GetProperty("total_marked_periods").GetInt32().Should().Be(0);
    }
}
