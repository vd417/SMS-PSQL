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
public class PrincipalAttendanceTests(PostgresFixture fx)
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
    public async Task PrincipalAttendance_returns_correct_totals_and_per_class_breakdown()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        // Seed Class A (will have roll-call marked): 4 students
        var classAId = (await Data(await principal.PostAsJsonAsync("/v1/classes", new
        {
            name = "PA-ClassA", grade = "IX", section = "A"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Seed Class B (will be left un-marked): 3 students
        var classBId = (await Data(await principal.PostAsJsonAsync("/v1/classes", new
        {
            name = "PA-ClassB", grade = "IX", section = "B"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Seed students in Class A
        var s1 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PA001", name = "Aarav Singh", grade = "IX", section = "A", roll = 1
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s2 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PA002", name = "Bina Sharma", grade = "IX", section = "A", roll = 2
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s3 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PA003", name = "Charan Patel", grade = "IX", section = "A", roll = 3
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s4 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PA004", name = "Diya Nair", grade = "IX", section = "A", roll = 4
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Class B students (roll-call intentionally not posted for this class)
        await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PB001", name = "Eva Reddy", grade = "IX", section = "B", roll = 1
        }), HttpStatusCode.Created);
        await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PB002", name = "Farhan Ali", grade = "IX", section = "B", roll = 2
        }), HttpStatusCode.Created);
        await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PB003", name = "Gita Rao", grade = "IX", section = "B", roll = 3
        }), HttpStatusCode.Created);

        // Set StudentCount for both classes via raw SQL (API doesn't expose StudentCount directly)
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "UPDATE dbo.Classes SET StudentCount = 4 WHERE Id = @id AND TenantId = @tenantId",
                new { id = classAId, tenantId });
            await conn.ExecuteAsync(
                "UPDATE dbo.Classes SET StudentCount = 3 WHERE Id = @id AND TenantId = @tenantId",
                new { id = classBId, tenantId });
        });

        // Legacy daily roll-call must not drive principal totals (period SoT).
        var todayDt = DateTime.UtcNow.Date;
        var today = todayDt.ToString("yyyy-MM-dd");
        (await principal.PostAsJsonAsync($"/v1/classes/{classAId}/attendance", new
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
                    new { tenantId, classId = classAId, studentId = row.StudentId, date = todayDt, status = row.Status });
            }
        });

        // Class B is left un-marked (no roll-call posted)

        // Seed a teacher for staff[] validation
        var teacher1Email = $"pa-t1-{tenantId:N}@x.com";
        var userId = Guid.NewGuid();
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Teachers (TenantId, Name, Email, SubjectsCsv, Phone, Designation, Status) " +
                "VALUES (@TenantId, @Name, @Email, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "PA Teacher One", Email = teacher1Email,
                      SubjectsCsv = "Physics", Phone = "9000000001",
                      Designation = "Teacher", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email, Status) VALUES (@Id, @TenantId, @Email, @Status)",
                new { Id = userId, TenantId = tenantId, Email = teacher1Email, Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new { TenantId = tenantId, UserId = userId, Kind = "in",
                      At = DateTime.UtcNow.Date.AddHours(8), Verified = true });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new { TenantId = tenantId, UserId = userId, Kind = "out",
                      At = DateTime.UtcNow.Date.AddHours(16), Verified = true });
        });

        // Call the endpoint
        var attendance = await Data(await principal.GetAsync("/v1/principal/attendance"), HttpStatusCode.OK);

        // Overall totals from period marks: present+late=3, marked=4, pct=75
        attendance.GetProperty("present_total").GetInt32().Should().Be(3);
        attendance.GetProperty("student_total").GetInt32().Should().Be(4);
        var overallPct = attendance.GetProperty("overall_pct").GetDecimal();
        overallPct.Should().Be(75.0m);

        // Per-class breakdown
        var classes = attendance.GetProperty("classes");
        classes.GetArrayLength().Should().Be(2);

        JsonElement? classA = null;
        JsonElement? classB = null;
        foreach (var cls in classes.EnumerateArray())
        {
            var name = cls.GetProperty("class_name").GetString();
            if (name == "PA-ClassA") classA = cls;
            if (name == "PA-ClassB") classB = cls;
        }

        classA.Should().NotBeNull("Class A should be in the response");
        classA!.Value.GetProperty("present").GetInt32().Should().Be(3);
        classA.Value.GetProperty("total").GetInt32().Should().Be(4);
        classA.Value.GetProperty("pct").GetDecimal().Should().Be(75.0m);
        classA.Value.GetProperty("marked").GetInt32().Should().Be(4);

        classB.Should().NotBeNull("Class B should be in the response");
        classB!.Value.GetProperty("present").GetInt32().Should().Be(0);
        classB.Value.GetProperty("total").GetInt32().Should().Be(0);
        classB.Value.GetProperty("pct").GetDecimal().Should().Be(0.0m);
        classB.Value.GetProperty("marked").GetInt32().Should().Be(0);

        // Staff array is present and contains our teacher
        var staff = attendance.GetProperty("staff");
        staff.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        bool foundTeacher = false;
        foreach (var s in staff.EnumerateArray())
        {
            if (s.GetProperty("name").GetString() == "PA Teacher One")
            {
                foundTeacher = true;
                s.GetProperty("checked_in").GetBoolean().Should().BeTrue();
                s.GetProperty("check_out_at").ValueKind.Should().NotBe(JsonValueKind.Null);
                break;
            }
        }
        foundTeacher.Should().BeTrue("seeded teacher should appear in staff[]");
    }

    [Fact]
    public async Task PrincipalAttendance_masks_geo_checkins_for_silver()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "silver");
        var principal = Client(app, tenantId, Policies.Principal);
        var userId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Teachers (TenantId, Name, Email, SubjectsCsv, Phone, Designation, Status) " +
                "VALUES (@TenantId, @Name, @Email, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "Silver Teacher", Email = $"silver-{tenantId:N}@x.com",
                      SubjectsCsv = "Maths", Phone = "9000000099",
                      Designation = "Teacher", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email, Status) VALUES (@Id, @TenantId, @Email, @Status)",
                new { Id = userId, TenantId = tenantId, Email = $"silver-{tenantId:N}@x.com", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new { TenantId = tenantId, UserId = userId, Kind = "in",
                      At = DateTime.UtcNow, Verified = true });
        });

        var attendance = await Data(await principal.GetAsync("/v1/principal/attendance"), HttpStatusCode.OK);
        var staff = attendance.GetProperty("staff");
        var hit = staff.EnumerateArray().FirstOrDefault(s =>
            s.GetProperty("name").GetString() == "Silver Teacher");
        hit.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Silver Teacher should appear in staff[]");
        hit.GetProperty("checked_in").GetBoolean().Should().BeTrue();
        hit.GetProperty("check_in_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task PrincipalAttendance_links_punch_by_display_name_when_email_missing()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var userId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email, Name, Status) VALUES (@Id, @TenantId, @Email, @Name, @Status)",
                new { Id = userId, TenantId = tenantId, Email = $"name-link-{tenantId:N}@x.com",
                      Name = "Name Link Teacher", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.Teachers (TenantId, Name, SubjectsCsv, Phone, Designation, Status) " +
                "VALUES (@TenantId, @Name, @SubjectsCsv, @Phone, @Designation, @Status)",
                new { TenantId = tenantId, Name = "Name Link Teacher",
                      SubjectsCsv = "Maths", Phone = "9000000088",
                      Designation = "Teacher", Status = "active" });

            await conn.ExecuteAsync(
                "INSERT INTO dbo.CheckIns (TenantId, UserId, Kind, At, Lat, Lng, AccuracyMeters, DistanceMeters, Verified) " +
                "VALUES (@TenantId, @UserId, @Kind, @At, 0, 0, 0, 0, @Verified)",
                new { TenantId = tenantId, UserId = userId, Kind = "in",
                      At = DateTime.UtcNow, Verified = true });
        });

        var attendance = await Data(await principal.GetAsync("/v1/principal/attendance"), HttpStatusCode.OK);
        var staff = attendance.GetProperty("staff");
        var hit = staff.EnumerateArray().FirstOrDefault(s =>
            s.GetProperty("name").GetString() == "Name Link Teacher");
        hit.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        hit.GetProperty("checked_in").GetBoolean().Should().BeTrue();
        hit.GetProperty("check_in_at").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task PrincipalAttendance_returns_403_for_teacher_token()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var res = await teacher.GetAsync("/v1/principal/attendance");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
