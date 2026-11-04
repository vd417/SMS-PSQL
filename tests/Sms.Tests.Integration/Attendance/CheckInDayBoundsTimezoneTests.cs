using FluentAssertions;
using Npgsql;
using Sms.Modules.Attendance;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Attendance;

/// Regression for the IST check-in bug: <see cref="CheckInRepository.LocalDayBoundsUtc"/> built the
/// day-window bounds with <see cref="DateTimeKind.Unspecified"/>, so Npgsql sent them as
/// <c>timestamp</c> (not <c>timestamptz</c>) and Postgres re-read them in the connection's session
/// time zone. Under an Asia/Kolkata session that shifts the whole window back 5h30m, which drops an
/// evening (IST) punch out of "today" — and GetTodayAsync then reports no check-in even though the
/// staff member checked in. The bounds must be UTC instants regardless of the DB session zone.
[Collection("sql")]
public class CheckInDayBoundsTimezoneTests(PostgresFixture fx)
{
    [Fact]
    public async Task GetToday_finds_an_evening_punch_when_the_db_session_is_not_utc()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId);

        // Same database, but the session runs in IST — exactly the condition that exposed the bug
        // locally (the app server's Postgres session was Asia/Kolkata).
        var kolkataCs = new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Timezone = "Asia/Kolkata" }
            .ConnectionString;
        var ctx = new TenantContext();
        ctx.Set(tenantId, userId, isPlatform: false);
        var repo = new CheckInRepository(new NpgsqlConnectionFactory(kolkataCs, ctx));

        var day = new DateOnly(2026, 9, 29);
        var istOffset = TimeSpan.FromHours(5.5);
        // 15:00Z == 20:30 IST on the 29th: an evening punch, unambiguously inside the IST day. It
        // sits in the last 5h30m of the correct window — precisely the slice the buggy session-zone
        // shift pushes past "today"'s end.
        var punchAt = new DateTime(2026, 9, 29, 15, 0, 0, DateTimeKind.Utc);

        await repo.ManualPunchAsync(tenantId, userId, "in", punchAt);

        var today = await repo.GetTodayAsync(userId, day, istOffset);

        today.CheckIn.Should().NotBeNull(
            "an evening IST punch belongs to that IST day regardless of the DB session time zone");
    }
}
