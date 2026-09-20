using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Attendance;

[Collection("sql")]
public class CheckinHistoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TeacherClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [Policies.Teacher], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task Seed(string cs, Guid tenantId, Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task History_returns_days_newest_first_with_check_in_and_check_out()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = TeacherClient(app, tenantId, userId);

        // Day A (2 days ago): verified in + out pair
        var dayA = DateTime.UtcNow.Date.AddDays(-2);
        var dayAIn = dayA.AddHours(8);
        var dayAOut = dayA.AddHours(16); // 8 hours span

        // Day B (1 day ago): only an in row, unverified (flagged)
        var dayB = DateTime.UtcNow.Date.AddDays(-1);
        var dayBIn = dayB.AddHours(9);

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new[]
                {
                    new { TenantId = tenantId, UserId = userId, Kind = "in",  At = dayAIn,  Verified = true },
                    new { TenantId = tenantId, UserId = userId, Kind = "out", At = dayAOut, Verified = true },
                    new { TenantId = tenantId, UserId = userId, Kind = "in",  At = dayBIn,  Verified = false },
                });
        });

        var res = await client.GetAsync("/v1/me/attendance/history?limit=30");
        var data = await Data(res, HttpStatusCode.OK);

        // Should return 2 day objects newest-first
        data.GetArrayLength().Should().Be(2);

        // First element = Day B (most recent, 1 day ago)
        var dayBElement = data[0];
        dayBElement.GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
        dayBElement.GetProperty("check_in").GetProperty("verified").GetBoolean().Should().BeFalse();
        dayBElement.GetProperty("check_out").ValueKind.Should().Be(JsonValueKind.Null);

        // Second element = Day A (2 days ago) with both check_in and check_out
        var dayAElement = data[1];
        dayAElement.GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
        dayAElement.GetProperty("check_in").GetProperty("verified").GetBoolean().Should().BeTrue();
        dayAElement.GetProperty("check_out").ValueKind.Should().NotBe(JsonValueKind.Null);
        dayAElement.GetProperty("check_out").GetProperty("verified").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Summary_returns_days_present_flagged_and_total_hours()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = TeacherClient(app, tenantId, userId);

        // Use a fixed past month so today's date doesn't bleed in
        var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        var dayA = month.AddDays(1); // 2nd of month
        var dayAIn = dayA.AddHours(8);
        var dayAOut = dayA.AddHours(16); // 8 hours, verified

        var dayB = month.AddDays(2); // 3rd of month
        var dayBIn = dayB.AddHours(9);
        // No out; Verified=false => flagged

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new[]
                {
                    new { TenantId = tenantId, UserId = userId, Kind = "in",  At = dayAIn,  Verified = true },
                    new { TenantId = tenantId, UserId = userId, Kind = "out", At = dayAOut, Verified = true },
                    new { TenantId = tenantId, UserId = userId, Kind = "in",  At = dayBIn,  Verified = false },
                });
        });

        var monthStr = month.ToString("yyyy-MM");
        var res = await client.GetAsync($"/v1/me/attendance/summary?month={monthStr}");
        var data = await Data(res, HttpStatusCode.OK);

        data.GetProperty("days_present").GetInt32().Should().Be(2);
        data.GetProperty("days_flagged").GetInt32().Should().Be(1);
        data.GetProperty("total_hours").GetDouble().Should().BeApproximately(8.0, 0.1);
    }

    [Fact]
    public async Task Summary_uses_local_month_with_offset_minutes()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = TeacherClient(app, tenantId, userId);

        // Aug 1 00:30 IST = Jul 31 19:00 UTC — must count in August when offset=330
        var punchUtc = new DateTime(2026, 7, 31, 19, 0, 0, DateTimeKind.Utc);

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, 'in', @At, 0, 0, 0, 15800, 0)",
                new { TenantId = tenantId, UserId = userId, At = punchUtc });
        });

        var res = await client.GetAsync("/v1/me/attendance/summary?month=2026-08&offset_minutes=330");
        var data = await Data(res, HttpStatusCode.OK);
        data.GetProperty("days_present").GetInt32().Should().Be(1);
        data.GetProperty("days_flagged").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Summary_returns_422_for_invalid_month_format()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = TeacherClient(app, tenantId, userId);

        var res = await client.GetAsync("/v1/me/attendance/summary?month=not-a-month");
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_month");
    }
}
