using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Sis;

[Collection("sql")]
public class ClassStudentsTests(PostgresFixture fx)
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

    // Seed a class via POST /v1/classes (admin token) and return its id.
    private static async Task<Guid> SeedClass(HttpClient client, string grade, string section)
    {
        var resp = await client.PostAsJsonAsync("/v1/classes", new
        {
            name = $"Class {grade}-{section}", grade, section
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    // Seed a student via POST /v1/students (admin token).
    private static async Task SeedStudent(HttpClient client, string name, string grade, string section)
    {
        var resp = await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-{Guid.NewGuid():N}",
            name,
            grade,
            section,
            roll = 1
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Lists_students_of_a_class_by_grade_and_section()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        // Admin client for seeding
        var admin = Client(app, tenantId, Policies.SchoolAdmin);

        var classId = await SeedClass(admin, "5", "A");
        await SeedStudent(admin, "Asha", "5", "A");
        await SeedStudent(admin, "Bims", "5", "A");
        await SeedStudent(admin, "Zed", "6", "B");

        // Teacher client for the actual request
        var teacher = Client(app, tenantId, Policies.Teacher);
        var resp = await teacher.GetAsync($"/v1/classes/{classId}/students?limit=50");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var names = json.GetProperty("data").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToArray();
        names.Should().BeEquivalentTo(["Asha", "Bims"]);
        json.GetProperty("next_cursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Paginates_with_cursor()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Client(app, tenantId, Policies.SchoolAdmin);
        var classId = await SeedClass(admin, "5", "A");

        foreach (var n in new[] { "A1", "A2", "A3" })
            await SeedStudent(admin, n, "5", "A");

        var teacher = Client(app, tenantId, Policies.Teacher);
        var page1Json = JsonDocument.Parse(
            await (await teacher.GetAsync($"/v1/classes/{classId}/students?limit=2")).Content.ReadAsStringAsync()
        ).RootElement;
        page1Json.GetProperty("data").GetArrayLength().Should().Be(2);
        var cursor = page1Json.GetProperty("next_cursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        var page2Json = JsonDocument.Parse(
            await (await teacher.GetAsync($"/v1/classes/{classId}/students?limit=2&cursor={cursor}")).Content.ReadAsStringAsync()
        ).RootElement;
        page2Json.GetProperty("data").GetArrayLength().Should().Be(1);
        page2Json.GetProperty("data")[0].GetProperty("name").GetString().Should().Be("A3");
    }

    [Fact]
    public async Task Student_role_is_forbidden()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var admin = Client(app, tenantId, Policies.SchoolAdmin);
        var classId = await SeedClass(admin, "5", "A");

        var studentClient = Client(app, tenantId, Policies.StudentOrParent);
        (await studentClient.GetAsync($"/v1/classes/{classId}/students"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
