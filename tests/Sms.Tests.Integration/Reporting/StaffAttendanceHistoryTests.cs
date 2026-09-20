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
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Reporting;

[Collection("sql")]
public class StaffAttendanceHistoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
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
    public async Task StaffAttendanceHistory_groups_two_days_of_punches_by_local_day()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var userId = Guid.NewGuid();
        var teacherEmail = $"sah-{tenantId:N}@x.com";
        Guid teacherId = default;

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            teacherId = await conn.ExecuteScalarAsync<Guid>(
                "INSERT INTO dbo.Teachers (TenantId, Name, Email, SubjectsCsv, Phone, Designation, Status) " +
                "OUTPUT inserted.Id " +
                "VALUES (@TenantId, @Name, @Email, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "History Teacher", Email = teacherEmail,
                      SubjectsCsv = "Science", Phone = "9000000077",
                      Designation = "Teacher", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email, Status) VALUES (@Id, @TenantId, @Email, @Status)",
                new { Id = userId, TenantId = tenantId, Email = teacherEmail, Status = "active" });

            var yesterday = DateTime.UtcNow.Date.AddDays(-1);
            var today = DateTime.UtcNow.Date;

            foreach (var (day, kind, hour) in new (DateTime Day, string Kind, int Hour)[]
            {
                (yesterday, "in", 8), (yesterday, "out", 16),
                (today, "in", 9), (today, "out", 17),
            })
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                    "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                    new { TenantId = tenantId, UserId = userId, Kind = kind, At = day.AddHours(hour), Verified = true });
            }
        });

        var history = await Data(
            await principal.GetAsync($"/v1/principal/staff/{teacherId}/attendance/history"), HttpStatusCode.OK);

        history.GetArrayLength().Should().Be(2);
        var days = history.EnumerateArray().ToList();
        // Most recent day first.
        days[0].GetProperty("check_in").GetProperty("kind").GetString().Should().Be("in");
        days[0].GetProperty("check_out").ValueKind.Should().NotBe(JsonValueKind.Null);
        days[1].GetProperty("check_in").ValueKind.Should().NotBe(JsonValueKind.Null);
        days[1].GetProperty("check_out").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task StaffAttendanceHistory_returns_empty_for_unknown_person()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        var history = await Data(
            await principal.GetAsync($"/v1/principal/staff/{Guid.NewGuid()}/attendance/history"), HttpStatusCode.OK);

        history.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task StaffAttendanceHistory_returns_403_for_teacher_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var res = await teacher.GetAsync($"/v1/principal/staff/{Guid.NewGuid()}/attendance/history");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
