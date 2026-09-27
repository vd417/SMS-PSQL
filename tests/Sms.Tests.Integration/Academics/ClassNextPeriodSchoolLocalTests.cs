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

// B-1: next_period must be computed against school-local (Asia/Kolkata) wall-clock day/time,
// not UTC. Fixed "now" = 04:30 UTC = 10:00 IST on a Monday (UTC day is still Sunday at that
// instant, and IST is 5:30 ahead - both the day and time differ from plain UTC).
[Collection("sql")]
public class ClassNextPeriodSchoolLocalTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task B1_NextPeriodUsesSchoolLocalDayAndTimeNotUtc()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var classId = Guid.NewGuid();

        // Fixed instant: 2026-09-28 (Monday) 04:30 UTC == 10:00 IST same Monday.
        // At this instant, UTC day-of-week is still "SUN" 2026-09-27 23:30 UTC... actually pick
        // a value where UTC day differs from IST day: 2026-09-27 20:00 UTC (Sunday) ==
        // 2026-09-28 01:30 IST (Monday). Slots are seeded on Monday IST wall-clock.
        var utcNow = new DateTime(2026, 9, 27, 20, 0, 0, DateTimeKind.Utc);
        var localNow = SchoolClock.ToSchoolLocal(utcNow);
        localNow.DayOfWeek.Should().Be(DayOfWeek.Monday);
        utcNow.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        app = app.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(new FixedClock(utcNow));
        }));

        // Slot at 10:00 local (Monday), after the fixed local "now" (01:30 local) -> should be
        // picked as next_period. A slot on "SUN" (the UTC day) must NOT be picked.
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Classes\" (\"Id\", \"TenantId\", \"Name\", \"StudentCount\") VALUES (@classId, @tenantId, 'C1', 0)",
                new { classId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TimetableSlots\" (\"TenantId\", \"Day\", \"Period\", \"ClassId\", \"Subject\", \"StartTime\") VALUES (@tenantId, 'MON', 1, @classId, 'Science', '10:00')",
                new { tenantId, classId });
            // A UTC-day ("SUN") slot that the OLD (buggy) code would have matched instead.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"TimetableSlots\" (\"TenantId\", \"Day\", \"Period\", \"ClassId\", \"Subject\", \"StartTime\") VALUES (@tenantId, 'SUN', 2, @classId, 'WrongDay', '23:59')",
                new { tenantId, classId });
        }

        // The JWT's own validity clock stays real time (matching ClassNextPeriodTests); only
        // the application's business clock (used by ClassRepository) is fixed above.
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync($"/v1/classes/{classId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        // RED (pre-fix): the SQL compares against now() AT TIME ZONE 'UTC', so at this fixed
        // instant the UTC day is "SUN" and UTC time is 20:00 -- neither seeded slot's
        // (day, time) pair matches under UTC rules, so next_period comes back null instead of
        // "Science".
        doc.RootElement.GetProperty("data").GetProperty("next_period").GetString().Should().Be("Science");
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow => utcNow;
    }
}
