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
using Xunit;

namespace Sms.Tests.Integration.Academics;

/// SD-5 (matrix row EXM-05): PATCH/DELETE /v1/exam-papers/{id} is creator-or-principal. A legacy
/// paper with CreatedBy = NULL is deliberately principal-only (NULL never counts as "mine").
[Collection("sql")]
public class ExamPaperOwnershipTests(PostgresFixture fx)
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
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<Guid> InsertPaperWithNullCreatedByAsync(Guid tenantId)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"ExamPapers\" (\"Id\", \"TenantId\", \"Name\", \"Subject\", \"MaxMarks\") " +
            "VALUES (@id, @tenantId, 'Legacy Paper', 'Legacy', 50)",
            new { id, tenantId });
        return id;
    }

    // ---- PATCH ----

    [Fact]
    public async Task Creator_teacher_can_patch_their_own_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var patched = await Data(await creator.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Renamed" }),
            HttpStatusCode.OK);
        patched.GetProperty("name").GetString().Should().Be("Renamed");
    }

    [Fact]
    public async Task Different_teacher_in_same_tenant_gets_403_on_patch()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var otherTeacher = Client(app, tenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var res = await otherTeacher.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Hijacked" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_can_patch_any_teachers_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var principal = Client(app, tenantId, Policies.Principal);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var patched = await Data(await principal.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Approved Edit" }),
            HttpStatusCode.OK);
        patched.GetProperty("name").GetString().Should().Be("Approved Edit");
    }

    [Fact]
    public async Task Null_createdby_paper_gives_teacher_403_and_principal_200_on_patch()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);
        var principal = Client(app, tenantId, Policies.Principal);

        var paperId = await InsertPaperWithNullCreatedByAsync(tenantId);

        var teacherRes = await teacher.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Should be blocked" });
        teacherRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var principalRes = await Data(
            await principal.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Principal edit" }), HttpStatusCode.OK);
        principalRes.GetProperty("name").GetString().Should().Be("Principal edit");
    }

    [Fact]
    public async Task Teacher_from_another_tenant_gets_404_not_403_on_patch()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var outsider = Client(app, otherTenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        // Cross-tenant: RLS hides the row entirely, so it's a 404 (never leaked as a 403).
        var getRes = await outsider.GetAsync($"/v1/exam-papers/{paperId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var patchRes = await outsider.PatchAsJsonAsync($"/v1/exam-papers/{paperId}", new { name = "Cross tenant" });
        patchRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- DELETE ----

    [Fact]
    public async Task Creator_teacher_can_delete_their_own_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var res = await creator.DeleteAsync($"/v1/exam-papers/{paperId}");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Different_teacher_in_same_tenant_gets_403_on_delete()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var otherTeacher = Client(app, tenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var res = await otherTeacher.DeleteAsync($"/v1/exam-papers/{paperId}");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Principal_can_delete_any_teachers_paper()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var principal = Client(app, tenantId, Policies.Principal);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var res = await principal.DeleteAsync($"/v1/exam-papers/{paperId}");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Null_createdby_paper_gives_teacher_403_and_principal_204_on_delete()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);
        var principal = Client(app, tenantId, Policies.Principal);

        var paperId = await InsertPaperWithNullCreatedByAsync(tenantId);

        var teacherRes = await teacher.DeleteAsync($"/v1/exam-papers/{paperId}");
        teacherRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var principalRes = await principal.DeleteAsync($"/v1/exam-papers/{paperId}");
        principalRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Teacher_from_another_tenant_gets_404_not_403_on_delete()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var creator = Client(app, tenantId, Policies.Teacher);
        var outsider = Client(app, otherTenantId, Policies.Teacher);

        var paper = await Data(await creator.PostAsJsonAsync("/v1/exam-papers", new { name = "Original", max_marks = 80 }),
            HttpStatusCode.Created);
        var paperId = paper.GetProperty("id").GetGuid();

        var res = await outsider.DeleteAsync($"/v1/exam-papers/{paperId}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
