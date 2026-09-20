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

namespace Sms.Tests.Integration.Parent;

/// Parent home in sms-student calls GET /v1/parents/me/children and unwraps
/// { data: StudentResponse[] } (id, name, admission_no, grade, section, …).
[Collection("sql")]
public class ParentChildrenTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient AsUser(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task Seed(Func<SqlConnection, Task> work)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private async Task<Guid> ParentUserId(string email, Guid tenantId)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        return await conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId
              AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@email)))
            """,
            new { email, tenantId });
    }

    [Fact]
    public async Task My_children_returns_the_student_linked_by_guardian_email()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var admission = $"ADM-CH-{Guid.NewGuid():N}"[..20];

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admission,
            name = "Ward One",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_name = "Ramesh Rana",
            guardian_phone = "9000000101",
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);
        var parent = AsUser(app, tenantId, parentId, Policies.StudentOrParent);
        var kids = await Data(await parent.GetAsync("/v1/parents/me/children"), HttpStatusCode.OK);

        kids.GetArrayLength().Should().Be(1);
        kids[0].GetProperty("id").GetGuid().Should().Be(created.GetProperty("id").GetGuid());
        kids[0].GetProperty("name").GetString().Should().Be("Ward One");
        kids[0].GetProperty("admission_no").GetString().Should().Be(admission);
        kids[0].GetProperty("grade").GetString().Should().Be("IV");
        kids[0].GetProperty("section").GetString().Should().Be("B");
        kids[0].GetProperty("guardian_email").GetString().Should().Be(parentEmail);
    }

    [Fact]
    public async Task My_children_returns_every_sibling_sharing_guardian_email()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var adm1 = $"ADM-S1-{Guid.NewGuid():N}"[..20];
        var adm2 = $"ADM-S2-{Guid.NewGuid():N}"[..20];

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = adm1,
            name = "Elder Kid",
            grade = "V",
            section = "A",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);
        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = adm2,
            name = "Younger Kid",
            grade = "III",
            section = "B",
            roll = 2,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);
        var parent = AsUser(app, tenantId, parentId, Policies.StudentOrParent);
        var kids = await Data(await parent.GetAsync("/v1/parents/me/children"), HttpStatusCode.OK);

        kids.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo("Elder Kid", "Younger Kid");
    }

    [Fact]
    public async Task My_children_returns_every_row_in_ParentStudentLinks_even_when_emails_differ()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var emailA = $"a{Guid.NewGuid():N}@home.test";
        var emailB = $"b{Guid.NewGuid():N}@home.test";

        var kidA = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-A-{Guid.NewGuid():N}"[..20],
            name = "Email A Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_email = emailA,
        }), HttpStatusCode.Created);
        var kidB = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-B-{Guid.NewGuid():N}"[..20],
            name = "Email B Kid",
            grade = "II",
            section = "B",
            roll = 2,
            guardian_email = emailB,
        }), HttpStatusCode.Created);

        var parentA = await ParentUserId(emailA, tenantId);
        var studentB = kidB.GetProperty("id").GetGuid();
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId)
                SELECT @parentA, @studentB, @tenantId
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.ParentStudentLinks
                    WHERE ParentUserId = @parentA AND StudentId = @studentB);
                """,
                new { parentA, studentB, tenantId });
        });

        var kids = await Data(
            await AsUser(app, tenantId, parentA, Policies.StudentOrParent)
                .GetAsync("/v1/parents/me/children"),
            HttpStatusCode.OK);

        kids.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo("Email A Kid", "Email B Kid");
        var ids = kids.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray();
        ids.Should().BeEquivalentTo([kidA.GetProperty("id").GetGuid(), studentB]);
    }

    [Fact]
    public async Task My_children_does_not_return_another_parent_s_child()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var emailA = $"a{Guid.NewGuid():N}@home.test";
        var emailB = $"b{Guid.NewGuid():N}@home.test";

        var kidA = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-A-{Guid.NewGuid():N}"[..20],
            name = "Parent A Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_email = emailA,
        }), HttpStatusCode.Created);
        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-B-{Guid.NewGuid():N}"[..20],
            name = "Parent B Kid",
            grade = "II",
            section = "B",
            roll = 2,
            guardian_email = emailB,
        }), HttpStatusCode.Created);

        var parentA = await ParentUserId(emailA, tenantId);
        var kids = await Data(
            await AsUser(app, tenantId, parentA, Policies.StudentOrParent)
                .GetAsync("/v1/parents/me/children"),
            HttpStatusCode.OK);

        kids.GetArrayLength().Should().Be(1);
        kids[0].GetProperty("id").GetGuid().Should().Be(kidA.GetProperty("id").GetGuid());
        kids[0].GetProperty("name").GetString().Should().Be("Parent A Kid");
    }

    [Fact]
    public async Task My_children_does_not_return_a_same_email_student_from_another_tenant()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var kidA = await Data(await Admin(app, tenantA).PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-TA-{Guid.NewGuid():N}"[..20],
            name = "Tenant A Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);
        await Data(await Admin(app, tenantB).PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-TB-{Guid.NewGuid():N}"[..20],
            name = "Tenant B Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentA = await ParentUserId(parentEmail, tenantA);
        var kids = await Data(
            await AsUser(app, tenantA, parentA, Policies.StudentOrParent)
                .GetAsync("/v1/parents/me/children"),
            HttpStatusCode.OK);

        kids.GetArrayLength().Should().Be(1);
        kids[0].GetProperty("id").GetGuid().Should().Be(kidA.GetProperty("id").GetGuid());
        kids[0].GetProperty("name").GetString().Should().Be("Tenant A Kid");
    }

    [Fact]
    public async Task My_children_returns_empty_array_when_parent_has_no_linked_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var parentEmail = $"empty{Guid.NewGuid():N}@home.test";

        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Users (Id, TenantId, Email, Phone, IsPlatform, Status, StudentId, MustSetPassword, Name)
                VALUES (@parentUserId, @tenantId, @parentEmail, NULL, 0, N'active', NULL, 1, N'No Kids');
                INSERT dbo.UserRoles (UserId, Role) VALUES (@parentUserId, N'student.parent');
                """,
                new { parentUserId, tenantId, parentEmail });
        });

        var parent = AsUser(app, tenantId, parentUserId, Policies.StudentOrParent);
        var kids = await Data(await parent.GetAsync("/v1/parents/me/children"), HttpStatusCode.OK);
        kids.GetArrayLength().Should().Be(0);
        kids.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task My_children_unauthenticated_returns_401()
    {
        await using var app = App();
        var anon = app.CreateClient();
        (await anon.GetAsync("/v1/parents/me/children"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creating_a_student_inserts_a_ParentStudentLinks_row_for_the_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var created = await Data(await Admin(app, tenantId).PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-LK-{Guid.NewGuid():N}"[..20],
            name = "Linked Ward",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);
        var studentId = created.GetProperty("id").GetGuid();
        int links = 0;
        await Seed(async conn =>
        {
            links = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM dbo.ParentStudentLinks
                WHERE ParentUserId = @parentId AND StudentId = @studentId AND TenantId = @tenantId
                """,
                new { parentId, studentId, tenantId });
        });
        links.Should().Be(1);
    }

    [Fact]
    public async Task My_children_does_not_return_a_guardian_email_match_without_a_ParentStudentLinks_row()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-NL-{Guid.NewGuid():N}"[..20],
            name = "Unlinked Email Kid",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var parentId = await ParentUserId(parentEmail, tenantId);
        var studentId = created.GetProperty("id").GetGuid();
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "DELETE FROM dbo.ParentStudentLinks WHERE ParentUserId = @parentId AND StudentId = @studentId",
                new { parentId, studentId });
        });

        var kids = await Data(
            await AsUser(app, tenantId, parentId, Policies.StudentOrParent)
                .GetAsync("/v1/parents/me/children"),
            HttpStatusCode.OK);

        kids.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task My_children_does_not_return_an_admission_match_without_a_ParentStudentLinks_row()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var guardianEmail = $"g{Guid.NewGuid():N}@home.test";
        var admission = $"ADM-AD-{Guid.NewGuid():N}"[..20];

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admission,
            name = "Admission Only Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_email = guardianEmail,
        }), HttpStatusCode.Created);

        var orphanParentId = Guid.NewGuid();
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.Users (Id, TenantId, Email, Phone, IsPlatform, Status, StudentId, MustSetPassword, Name)
                VALUES (@orphanParentId, @tenantId, @email, NULL, 0, N'active', @admission, 1, N'Admission Parent');
                INSERT dbo.UserRoles (UserId, Role) VALUES (@orphanParentId, N'student.parent');
                """,
                new
                {
                    orphanParentId,
                    tenantId,
                    email = $"adm{Guid.NewGuid():N}@home.test",
                    admission,
                });
        });

        var kids = await Data(
            await AsUser(app, tenantId, orphanParentId, Policies.StudentOrParent)
                .GetAsync("/v1/parents/me/children"),
            HttpStatusCode.OK);

        kids.GetArrayLength().Should().Be(0);
    }
}
