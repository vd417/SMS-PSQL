using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class ClassesScopingTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient ClientForUser(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, roles, isPlatform: false);
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

    [Fact]
    public async Task Teacher_only_sees_their_own_class_teacher_class_and_slot_taught_class()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var admin = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);
        var teacher = ClientForUser(app, tenantId, teacherUserId, Policies.Teacher);

        var homeroomClassId = (await Data(await admin.PostAsJsonAsync("/v1/classes",
            new { name = "Homeroom", grade = "IX", section = "A" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var taughtClassId = (await Data(await admin.PostAsJsonAsync("/v1/classes",
            new { name = "Taught", grade = "IX", section = "B" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var otherClassId = (await Data(await admin.PostAsJsonAsync("/v1/classes",
            new { name = "Other", grade = "IX", section = "C" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            var teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, 'T1', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                "UPDATE dbo.Classes SET ClassTeacherId = @teacherId WHERE Id = @homeroomClassId",
                new { teacherId, homeroomClassId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (Id, TenantId, [Day], Period, Subject, ClassId, TeacherId) " +
                "VALUES (NEWID(), @tenantId, 'Mon', 1, 'Science', @taughtClassId, @teacherId)",
                new { tenantId, taughtClassId, teacherId });
        }

        var list = await Data(await teacher.GetAsync("/v1/classes"), HttpStatusCode.OK);
        var ids = list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToArray();
        ids.Should().Contain(homeroomClassId);
        ids.Should().Contain(taughtClassId);
        ids.Should().NotContain(otherClassId);
    }

    [Fact]
    public async Task Admin_still_sees_every_class_unscoped()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);

        var classId = (await Data(await admin.PostAsJsonAsync("/v1/classes",
            new { name = "X-A", grade = "X", section = "A" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        var list = await Data(await admin.GetAsync("/v1/classes"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(classId);
    }

    [Fact]
    public async Task Principal_still_sees_every_class_unscoped()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);
        var principal = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.Principal);

        var classId = (await Data(await admin.PostAsJsonAsync("/v1/classes",
            new { name = "X-D", grade = "X", section = "D" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        var list = await Data(await principal.GetAsync("/v1/classes"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(classId);
    }

    [Fact]
    public async Task Teacher_with_no_linkage_sees_no_classes()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);
        var teacher = ClientForUser(app, tenantId, Guid.NewGuid(), Policies.Teacher);

        await admin.PostAsJsonAsync("/v1/classes", new { name = "X-E", grade = "X", section = "E" });

        var list = await Data(await teacher.GetAsync("/v1/classes"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(0);
    }
}
