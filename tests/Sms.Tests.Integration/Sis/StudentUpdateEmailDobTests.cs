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

namespace Sms.Tests.Integration.Sis;

/// PATCH /students/{id} used to silently drop Gender/Dob/Email/Address —
/// UpdateStudentRequest never had these properties, so a student's email
/// (etc.) could never be changed after creation. Regression coverage for
/// that fix.
[Collection("sql")]
public class StudentUpdateEmailDobTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> AppWithDb(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static async Task SeedStudentAsync(PostgresFixture fx, Guid tenantId, Guid studentId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Gender", "Email", "Status")
            VALUES (@studentId, @tenantId, 'A1', 'S1', 'M', 'old@example.com', 'active')
            """,
            new { studentId, tenantId });
    }

    private static string TeacherToken(Guid tenantId) =>
        new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock())
        .IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);

    [Fact]
    public async Task Updating_a_student_persists_gender_dob_email_and_address()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var patch = await client.PatchAsJsonAsync($"/v1/students/{studentId}", new
        {
            gender = "F",
            dob = "2015-04-12T00:00:00",
            email = "new@example.com",
            address = "221B Baker Street",
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()))
        {
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("gender").GetString().Should().Be("F");
            data.GetProperty("email").GetString().Should().Be("new@example.com");
            data.GetProperty("address").GetString().Should().Be("221B Baker Street");
        }

        // Re-fetch to prove it was actually written to the row, not just echoed back.
        var get = await client.GetAsync($"/v1/students/{studentId}");
        using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var getData = getDoc.RootElement.GetProperty("data");
        getData.GetProperty("gender").GetString().Should().Be("F");
        getData.GetProperty("email").GetString().Should().Be("new@example.com");
        getData.GetProperty("address").GetString().Should().Be("221B Baker Street");
    }

    [Fact]
    public async Task Updating_unrelated_fields_leaves_email_untouched()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var res = await client.PatchAsJsonAsync($"/v1/students/{studentId}", new { name = "S1 Renamed" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("S1 Renamed");
        data.GetProperty("email").GetString().Should().Be("old@example.com");
    }

    [Fact]
    public async Task Updating_a_student_persists_guardian_email()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var patch = await client.PatchAsJsonAsync($"/v1/students/{studentId}", new
        {
            guardian_email = "Vaibhavv@yopmail.com",
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("data").GetProperty("guardian_email").GetString()
                .Should().Be("Vaibhavv@yopmail.com");
        }

        var get = await client.GetAsync($"/v1/students/{studentId}");
        using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        getDoc.RootElement.GetProperty("data").GetProperty("guardian_email").GetString()
            .Should().Be("Vaibhavv@yopmail.com");
    }

    [Fact]
    public async Task Updating_a_student_persists_avatar_hue()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var patch = await client.PatchAsJsonAsync($"/v1/students/{studentId}", new { avatar_hue = 210 });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()))
            doc.RootElement.GetProperty("data").GetProperty("avatar_hue").GetInt32().Should().Be(210);

        var get = await client.GetAsync($"/v1/students/{studentId}");
        using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        getDoc.RootElement.GetProperty("data").GetProperty("avatar_hue").GetInt32().Should().Be(210);

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.is_platform', '1', false)");
        var stored = await conn.ExecuteScalarAsync<int>(
            """SELECT "AvatarHue" FROM "dbo"."Students" WHERE "Id" = @studentId""", new { studentId });
        stored.Should().Be(210);
    }
}
