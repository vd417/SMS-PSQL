using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ClassNextPeriodTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Class_next_period_reflects_upcoming_timetable_slot()
    {
        // Fix the application clock to a mid-day school-local instant so this test never depends on
        // real wall-clock: 2026-09-28 04:30 UTC == 10:00 IST (Monday). Previously it derived the
        // slot from DateTime.UtcNow as "now + 1h" while keeping today's day name; when the suite ran
        // between 23:00-24:00 IST that hour wrapped past midnight, leaving the slot in the past so
        // next_period came back null — a time-of-day flake that failed CI in that window. Only the
        // business clock (used by ClassRepository) is fixed; the JWT keeps real time.
        var utcNow = new DateTime(2026, 9, 28, 4, 30, 0, DateTimeKind.Utc);
        var localNow = SchoolClock.ToSchoolLocal(utcNow);
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureServices(services => services.AddSingleton<IClock>(new FixedClock(utcNow)));
        });
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();
        // next_period is computed against the school-local (Asia/Kolkata) day/time, not UTC, since
        // TimetableSlots store school-local wall-clock values.
        var today3LetterDay = localNow.ToString("ddd"); // e.g. "Mon"
        var future = localNow.AddHours(1).ToString("HH:mm");

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"Id\", \"TenantId\", \"Name\", \"StudentCount\") VALUES (@classId, @tenantId, 'C1', 0)",
                new { classId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TimetableSlots\" (\"TenantId\", \"Day\", \"Period\", \"ClassId\", \"Subject\", \"StartTime\") VALUES (@tenantId, @day, 1, @classId, 'Science', @startTime)",
                new { tenantId, day = today3LetterDay, classId, startTime = future });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/classes/{classId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("next_period").GetString().Should().Be("Science");
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
    }
}
