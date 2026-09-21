using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Reporting;

[Collection("sql")]
public class PrincipalOverviewTests(PostgresFixture fx)
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
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, because: body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    // Raw SQL seeding helper that sets the tenant session context for RLS
    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task PrincipalOverview_returns_correct_kpis_and_staff()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        // Seed a class with known StudentCount via POST
        var classId = (await Data(await principal.PostAsJsonAsync("/v1/classes", new
        {
            name = "X-A", grade = "X", section = "A"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Seed 4 students
        var s1 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PO001", name = "Alice Jones", grade = "X", section = "A", roll = 1
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s2 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PO002", name = "Bob Smith", grade = "X", section = "A", roll = 2
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s3 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PO003", name = "Carol Lee", grade = "X", section = "A", roll = 3
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s4 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PO004", name = "Dave Patel", grade = "X", section = "A", roll = 4
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Roll-call: 2 present, 1 late, 1 absent => present+late = 3 out of 4 students
        // StudentsPresentPct = 100 * 3 / SUM(StudentCount)
        // But StudentCount on Classes may not be set automatically; attendance records count is used
        // students_present_pct = 100 * (present+late count from AttendanceRecords) / SUM(Classes.StudentCount)
        // We need StudentCount. Let's update the class StudentCount via raw SQL.
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "UPDATE dbo.Classes SET StudentCount = 4 WHERE Id = @classId AND TenantId = @tenantId",
                new { classId, tenantId });
        });

        var todayDt = DateTime.UtcNow.Date;
        var today = todayDt.ToString("yyyy-MM-dd");
        (await principal.PostAsJsonAsync($"/v1/classes/{classId}/attendance", new
        {
            date = today,
            records = new[]
            {
                new { student_id = s1, status = "present" },
                new { student_id = s2, status = "late" },
                new { student_id = s3, status = "present" },
                new { student_id = s4, status = "absent" }
            }
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            foreach (var row in new (Guid StudentId, string Status)[]
            {
                (s1, "present"), (s2, "late"), (s3, "present"), (s4, "absent"),
            })
            {
                await conn.ExecuteAsync(@"
INSERT dbo.PeriodAttendanceRecords
  (Id, TenantId, ClassId, StudentId, [Date], Period, Subject, Status, CreatedAt, UpdatedAt)
VALUES
  (NEWID(), @tenantId, @classId, @studentId, @date, 1, N'Math', @status, SYSUTCDATETIME(), SYSUTCDATETIME())",
                    new { tenantId, classId, studentId = row.StudentId, date = todayDt, status = row.Status });
            }
        });

        // Email bridge seeding:
        // Teacher 1 (checked-in): email = t1-po@x.com
        // Teacher 2 (no check-in): email = t2-po@x.com
        var teacher1Email = $"t1-{tenantId:N}@x.com";
        var teacher2Email = $"t2-{tenantId:N}@x.com";
        var userId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            // Teacher 1 - active, with email matching a Users row
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Teachers (TenantId, Name, Email, SubjectsCsv, Phone, Designation, Status) " +
                "VALUES (@TenantId, @Name, @Email, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "Alice Teacher", Email = teacher1Email,
                      SubjectsCsv = "Maths,Science", Phone = "9876543210",
                      Designation = "Senior Teacher", Status = "active" });

            // Teacher 2 - active, no matching user or check-in
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Teachers (TenantId, Name, Email, SubjectsCsv, Phone, Designation, Status) " +
                "VALUES (@TenantId, @Name, @Email, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "Bob Teacher", Email = teacher2Email,
                      SubjectsCsv = "English", Phone = "9876543211",
                      Designation = "Teacher", Status = "active" });

            // Users row matching teacher 1's email (no RLS on Users)
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email, Status) VALUES (@Id, @TenantId, @Email, @Status)",
                new { Id = userId, TenantId = tenantId, Email = teacher1Email, Status = "active" });

            // CheckIns row for that user - verified 'in' today
            await conn.ExecuteAsync(
                "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new { TenantId = tenantId, UserId = userId, Kind = "in",
                      At = DateTime.UtcNow, Verified = true });

            // LeaveRequest with Status='pending'
            await conn.ExecuteAsync(
                "INSERT INTO dbo.LeaveRequests (TenantId, RequesterId, Type, Status) " +
                "VALUES (@TenantId, @RequesterId, @Type, @Status)",
                new { TenantId = tenantId, RequesterId = userId, Type = "casual", Status = "pending" });
        });

        // Call the endpoint
        var overview = await Data(await principal.GetAsync("/v1/principal/overview"), HttpStatusCode.OK);

        // Assert KPIs
        var kpis = overview.GetProperty("kpis");
        kpis.GetProperty("staff_total").GetInt32().Should().Be(2);
        kpis.GetProperty("staff_present").GetInt32().Should().Be(1);
        kpis.GetProperty("pending_approvals").GetInt32().Should().Be(1);

        // students_present_pct: 100.0 * 3 / 4 = 75.0
        var pct = kpis.GetProperty("students_present_pct").GetDecimal();
        pct.Should().Be(75.0m);

        // Assert staff entries
        var staff = overview.GetProperty("staff");
        staff.GetArrayLength().Should().Be(2);

        // Find the checked-in teacher (Alice Teacher)
        JsonElement? checkedInEntry = null;
        JsonElement? notCheckedInEntry = null;
        foreach (var entry in staff.EnumerateArray())
        {
            if (entry.GetProperty("checked_in").GetBoolean())
                checkedInEntry = entry;
            else
                notCheckedInEntry = entry;
        }

        checkedInEntry.Should().NotBeNull();
        checkedInEntry!.Value.GetProperty("name").GetString().Should().Be("Alice Teacher");
        checkedInEntry.Value.GetProperty("initials").GetString().Should().Be("AT");
        checkedInEntry.Value.GetProperty("subject").GetString().Should().Be("Maths");
        checkedInEntry.Value.GetProperty("phone").GetString().Should().Be("9876543210");
        checkedInEntry.Value.GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);
        checkedInEntry.Value.GetProperty("designation").GetString().Should().Be("Senior Teacher");
        checkedInEntry.Value.GetProperty("check_in_at").ValueKind.Should().NotBe(JsonValueKind.Null);

        notCheckedInEntry.Should().NotBeNull();
        notCheckedInEntry!.Value.GetProperty("name").GetString().Should().Be("Bob Teacher");
        notCheckedInEntry.Value.GetProperty("initials").GetString().Should().Be("BT");
        notCheckedInEntry.Value.GetProperty("checked_in").GetBoolean().Should().BeFalse();
        notCheckedInEntry.Value.GetProperty("check_in_at").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PrincipalOverview_returns_403_for_teacher_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var res = await teacher.GetAsync("/v1/principal/overview");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
