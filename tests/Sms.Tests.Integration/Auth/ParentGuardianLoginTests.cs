using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Auth;

/// Parent app login looks up Users by email. Enrolment stores father/mother
/// mail on the student — without a parent Users row, forgot-password/OTP
/// always returns 404 "Email is not registered."
[Collection("sql")]
public class ParentGuardianLoginTests(PostgresFixture fx)
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

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Creating_a_student_with_guardian_email_registers_parent_login()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var studentEmail = $"kid{Guid.NewGuid():N}@school.test";

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-PAR-1",
            name = "Ward One",
            grade = "IV",
            section = "B",
            roll = 1,
            email = studentEmail,
            guardian_name = "Ramesh Rana",
            guardian_phone = "9000000001",
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);
        created.GetProperty("guardian_email").GetString().Should().Be(parentEmail);

        var listed = await Data(await admin.GetAsync("/v1/students"), HttpStatusCode.OK);
        listed.EnumerateArray().Select(e => e.GetProperty("guardian_email").GetString())
            .Should().Contain(parentEmail);

        var anon = app.CreateClient();
        var forgot = await anon.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = parentEmail });
        forgot.StatusCode.Should().Be(HttpStatusCode.OK, await forgot.Content.ReadAsStringAsync());

        created.GetProperty("email").GetString().Should().Be(studentEmail);
        var forgotStudent = await anon.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = studentEmail, role = "student" });
        forgotStudent.StatusCode.Should().Be(HttpStatusCode.OK, await forgotStudent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forgot_delivers_otp_to_parent_mail_when_role_is_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var studentEmail = $"kid{Guid.NewGuid():N}@school.test";

        await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-OTP-ROLE",
            name = "Ward Otp",
            grade = "IV",
            section = "B",
            roll = 8,
            email = studentEmail,
            guardian_name = "Ramesh",
            guardian_phone = "9000000088",
            guardian_email = parentEmail,
        }), HttpStatusCode.Created);

        var anon = app.CreateClient();
        var parentForgot = await Data(await anon.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = parentEmail, role = "parent" }), HttpStatusCode.OK);
        parentForgot.GetProperty("channel").GetString().Should().Be("email");
        parentForgot.GetProperty("recipient").GetString().Should().Be("self");
        parentForgot.GetProperty("sent_to").GetString().Should().Contain("@home.test");
        parentForgot.GetProperty("sent_to").GetString().Should().NotContain("@school.test");

        var studentForgot = await Data(await anon.PostAsJsonAsync("/v1/auth/password/forgot",
            new { identifier = studentEmail, role = "student" }), HttpStatusCode.OK);
        studentForgot.GetProperty("sent_to").GetString().Should().Contain("@school.test");
        studentForgot.GetProperty("sent_to").GetString().Should().NotContain("@home.test");
        studentForgot.GetProperty("recipient").GetString().Should().Be("self");
    }

    [Fact]
    public async Task Saving_father_email_in_extras_registers_parent_login()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"mom{Guid.NewGuid():N}@home.test";

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-PAR-2",
            name = "Ward Two",
            grade = "IV",
            section = "B",
            roll = 2,
            guardian_name = "Meera",
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        await Data(await admin.PutAsJsonAsync($"/v1/students/{id}/extras", new
        {
            extras_json = "{\"father\":{\"name\":\"Meera\",\"email\":\"" + parentEmail + "\"}}",
        }), HttpStatusCode.OK);

        var got = await Data(await admin.GetAsync($"/v1/students/{id}"), HttpStatusCode.OK);
        got.GetProperty("guardian_email").GetString().Should().Be(parentEmail);

        var anon = app.CreateClient();
        (await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = parentEmail }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Extras_parent_email_registers_login_when_student_already_has_guardian_phone()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        const string sharedPhone = "9000000099";

        var created = await Data(await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "ADM-PAR-3",
            name = "Ward Three",
            grade = "IV",
            section = "B",
            roll = 3,
            guardian_name = "Vaibhav Dubey",
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var adm = created.GetProperty("admission_no").GetString();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                "UPDATE \"dbo\".\"Students\" SET \"GuardianPhone\" = @phone WHERE \"Id\" = @id",
                new { phone = sharedPhone, id });
            await conn.ExecuteAsync(
                "UPDATE \"dbo\".\"Users\" SET \"Phone\" = @phone WHERE \"StudentId\" = @adm",
                new { phone = sharedPhone, adm });
        }

        var put = await admin.PutAsJsonAsync($"/v1/students/{id}/extras", new
        {
            extras_json = "{\"father\":{\"name\":\"Vaibhav Dubey\",\"email\":\"" + parentEmail + "\"}}",
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var got = await Data(await admin.GetAsync($"/v1/students/{id}"), HttpStatusCode.OK);
        got.GetProperty("guardian_email").GetString().Should().Be(parentEmail);

        var anon = app.CreateClient();
        (await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = parentEmail }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patching_student_succeeds_when_guardian_phone_already_on_student_login()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var studentUserId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var admin = Admin(app, tenantId);
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        const string sharedPhone = "7080080089";
        const string adm = "sccrdtb/STU/26/PATCH";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                """
                INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Gender", "Email", "GuardianEmail", "GuardianPhone", "GuardianName", "Status")
                VALUES (@studentId, @tenantId, @adm, 'Rahul Sharma', 'M', 'rahul@patch.test', @parentEmail, @phone, 'Vaibhav Dubey', 'active');
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
                VALUES (@studentUserId, @tenantId, 'rahul@patch.test', @phone, false, 'active', @adm, false, 'Rahul Sharma');
                INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (@studentUserId, 'student');
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
                VALUES (@parentUserId, @tenantId, @parentEmail, NULL, false, 'active', @adm, true, 'Vaibhav Dubey');
                INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (@parentUserId, 'student.parent');
                """,
                new { studentId, tenantId, adm, parentEmail, phone = sharedPhone, studentUserId, parentUserId });
        }

        var patch = await admin.PatchAsJsonAsync($"/v1/students/{studentId}", new
        {
            guardian_email = parentEmail,
            guardian_phone = sharedPhone,
            name = "Rahul Sharma",
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());

        var got = await Data(await admin.GetAsync($"/v1/students/{studentId}"), HttpStatusCode.OK);
        got.GetProperty("guardian_email").GetString().Should().Be(parentEmail);

        var anon = app.CreateClient();
        var forgotParent = await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = parentEmail });
        forgotParent.StatusCode.Should().Be(HttpStatusCode.OK, await forgotParent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forgot_admission_succeeds_when_guardian_phone_already_on_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var parentEmail = $"dad{Guid.NewGuid():N}@home.test";
        var studentEmail = $"kid{Guid.NewGuid():N}@school.test";
        const string sharedPhone = "7111987654";
        const string adm = "FG-PHONE-ADM-1";

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
            await conn.ExecuteAsync(
                """
                INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Email", "GuardianEmail", "GuardianPhone", "GuardianName", "Status")
                VALUES (@studentId, @tenantId, @adm, 'Ward Phone', @studentEmail, @parentEmail, @phone, 'Dad', 'active');
                INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "Phone", "IsPlatform", "Status", "StudentId", "MustSetPassword", "Name")
                VALUES (@parentUserId, @tenantId, @parentEmail, @phone, false, 'active', @adm, true, 'Dad');
                INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (@parentUserId, 'student.parent');
                """,
                new { studentId, tenantId, adm, studentEmail, parentEmail, phone = sharedPhone, parentUserId });
        }

        var anon = app.CreateClient();
        var forgotAdm = await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = adm });
        forgotAdm.StatusCode.Should().Be(HttpStatusCode.OK, await forgotAdm.Content.ReadAsStringAsync());
        var forgotKid = await anon.PostAsJsonAsync("/v1/auth/password/forgot", new { identifier = studentEmail });
        forgotKid.StatusCode.Should().Be(HttpStatusCode.OK, await forgotKid.Content.ReadAsStringAsync());
    }
}
