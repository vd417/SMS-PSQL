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

[Collection("sql")]
public class StudentPhotoTests(PostgresFixture fx)
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
            """INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name", "Status") VALUES (@studentId, @tenantId, 'A1', 'S1', 'active')""",
            new { studentId, tenantId });
    }

    private static string TeacherToken(Guid tenantId) =>
        new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock())
        .IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Teacher }, isPlatform: false);

    [Fact]
    public async Task Teacher_sets_and_then_clears_a_students_photo()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var set = await client.PatchAsJsonAsync($"/v1/students/{studentId}",
            new { photo_url = "https://cdn.example.com/students/a.png", set_photo = true });
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var setDoc = JsonDocument.Parse(await set.Content.ReadAsStringAsync()))
            setDoc.RootElement.GetProperty("data").GetProperty("photo_url").GetString()
                .Should().Be("https://cdn.example.com/students/a.png");

        var get1 = await client.GetAsync($"/v1/students/{studentId}");
        using (var doc1 = JsonDocument.Parse(await get1.Content.ReadAsStringAsync()))
            doc1.RootElement.GetProperty("data").GetProperty("photo_url").GetString()
                .Should().Be("https://cdn.example.com/students/a.png");

        var clear = await client.PatchAsJsonAsync($"/v1/students/{studentId}",
            new { photo_url = (string?)null, set_photo = true });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        var get2 = await client.GetAsync($"/v1/students/{studentId}");
        using var doc2 = JsonDocument.Parse(await get2.Content.ReadAsStringAsync());
        doc2.RootElement.GetProperty("data").GetProperty("photo_url").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Updating_other_fields_without_set_photo_leaves_the_photo_untouched()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        await client.PatchAsJsonAsync($"/v1/students/{studentId}",
            new { photo_url = "https://cdn.example.com/students/a.png", set_photo = true });

        // A plain field edit that doesn't set set_photo must not wipe the existing photo.
        var res = await client.PatchAsJsonAsync($"/v1/students/{studentId}", new { name = "S1 Renamed" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("name").GetString().Should().Be("S1 Renamed");
        doc.RootElement.GetProperty("data").GetProperty("photo_url").GetString()
            .Should().Be("https://cdn.example.com/students/a.png");
    }

    [Fact]
    public async Task Rejects_a_photo_value_that_is_not_a_data_uri_or_http_url()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var res = await client.PatchAsJsonAsync($"/v1/students/{studentId}",
            new { photo_url = "not-a-valid-value", set_photo = true });
        res.StatusCode.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Rejects_a_photo_value_over_400000_characters()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await SeedStudentAsync(fx, tenantId, studentId);

        await using var app = AppWithDb(fx);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TeacherToken(tenantId));

        var oversized = "data:image/png;base64," + new string('a', 400_001);
        var res = await client.PatchAsJsonAsync($"/v1/students/{studentId}",
            new { photo_url = oversized, set_photo = true });
        res.StatusCode.Should().Be((HttpStatusCode)422);
    }
}
