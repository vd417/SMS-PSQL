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

// SD-2/B-2: total/student_total = active headcount (same basis as /classes student_count),
// present = DISTINCT students marked present/late in ANY period that day (not a period-mark
// count). PRN-01 kpis.students_present_pct must use the same semantics.
[Collection("sql")]
public class PrincipalAttendanceHeadcountTests(PostgresFixture fx)
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

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task B2_ClassTotalIsActiveHeadcountAndPresentIsDistinctStudents()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        var classId = (await Data(await principal.PostAsJsonAsync("/v1/classes", new
        {
            name = "PH-ClassA", grade = "X", section = "A"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var s1 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PH001", name = "Aisha Khan", grade = "X", section = "A", roll = 1
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var s2 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PH002", name = "Bilal Ahmed", grade = "X", section = "A", roll = 2
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var s3 = (await Data(await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PH003", name = "Chetan Rao", grade = "X", section = "A", roll = 3
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var today = DateTime.UtcNow.Date;

        // s1 present in P1 and P2 (must count once, not twice), s2 present in P1 only, s3 absent.
        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            foreach (var row in new (Guid StudentId, int Period, string Status)[]
            {
                (s1, 1, "present"), (s1, 2, "present"), (s2, 1, "present"), (s3, 1, "absent"),
            })
            {
                await conn.ExecuteAsync(@"
INSERT INTO ""dbo"".""PeriodAttendanceRecords""
  (""Id"", ""TenantId"", ""ClassId"", ""StudentId"", ""Date"", ""Period"", ""Subject"", ""Status"", ""CreatedAt"", ""UpdatedAt"")
VALUES
  (gen_random_uuid(), @tenantId, @classId, @studentId, @date, @period, 'Math', @status, now() AT TIME ZONE 'UTC', now() AT TIME ZONE 'UTC')",
                    new { tenantId, classId, studentId = row.StudentId, date = today, period = row.Period, status = row.Status });
            }
        });

        // A second tenant with its own class/students/marks must never leak into tenant 1's totals.
        var otherTenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, otherTenantId, tier: "platinum");
        var otherPrincipal = Client(app, otherTenantId, Policies.Principal);
        await otherPrincipal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "OT001", name = "Other Tenant Student", grade = "X", section = "A", roll = 1
        });

        var attendance = await Data(await principal.GetAsync("/v1/principal/attendance"), HttpStatusCode.OK);
        var classes = attendance.GetProperty("classes");
        JsonElement? classA = null;
        foreach (var cls in classes.EnumerateArray())
        {
            if (cls.GetProperty("class_name").GetString() == "PH-ClassA") classA = cls;
        }
        classA.Should().NotBeNull();
        // RED (pre-fix): "total"/"marked" are period-mark counts (4), and "present" counts every
        // present/late MARK (2 for s1 + 1 for s2 = 3), not distinct students (2).
        classA!.Value.GetProperty("total").GetInt32().Should().Be(3, "class active headcount, not period-mark count");
        classA.Value.GetProperty("present").GetInt32().Should().Be(2, "distinct students present/late, not mark count");
        classA.Value.GetProperty("pct").GetDecimal().Should().BeApproximately(66.7m, 0.05m);

        // Other tenant's single student must not appear in this tenant's totals.
        attendance.GetProperty("student_total").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task B2_SchoolStudentTotalIsHeadcountEvenWithZeroMarks()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);

        await principal.PostAsJsonAsync("/v1/classes", new { name = "PH2-ClassA", grade = "XI", section = "A" });
        await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PH2-001", name = "Deepa Iyer", grade = "XI", section = "A", roll = 1
        });
        await principal.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "PH2-002", name = "Esha Kapoor", grade = "XI", section = "A", roll = 2
        });

        // No attendance marks posted anywhere for this tenant today.
        var attendance = await Data(await principal.GetAsync("/v1/principal/attendance"), HttpStatusCode.OK);
        // RED (pre-fix): student_total is a COUNT of PeriodAttendanceRecords rows -> 0, even
        // though 2 active students exist.
        attendance.GetProperty("student_total").GetInt32().Should().Be(2);
        attendance.GetProperty("present_total").GetInt32().Should().Be(0);

        var overview = await Data(await principal.GetAsync("/v1/principal/overview"), HttpStatusCode.OK);
        // PRN-01 must use the same semantics: 0 present / 2 headcount = 0%.
        overview.GetProperty("kpis").GetProperty("students_present_pct").GetDecimal().Should().Be(0.0m);
    }
}
