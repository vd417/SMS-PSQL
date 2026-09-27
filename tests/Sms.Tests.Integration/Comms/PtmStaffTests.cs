using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class PtmStaffTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task Teacher_creates_meeting_for_self()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");

        var res = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("teacher_id").GetGuid().Should().Be(seed.Teacher1Id);
        data.GetProperty("status").GetString().Should().Be("pending");
        data.GetProperty("student_name").GetString().Should().Be("Student One");
    }

    [Fact]
    public async Task Teacher_cannot_create_for_other_teacher()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");

        var res = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, teacher_id = seed.Teacher2Id, subject = "Maths",
            date = "2026-10-10", time = "10:30", mode = "Video call",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("teacher_id").GetGuid().Should().Be(seed.Teacher1Id);
    }

    [Fact]
    public async Task Admin_must_name_teacher()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.AdminUserId, seed.TenantId, "school.admin");

        var noTeacher = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        noTeacher.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(noTeacher)).Should().Be("validation_failed");

        var withTeacher = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, teacher_id = seed.Teacher2Id, subject = "Maths",
            date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        withTeacher.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_rejects_bad_date_and_unknown_student()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");

        var badDate = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "10/10/2026", time = "10:30", mode = "Video call",
        });
        badDate.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(badDate)).Should().Be("validation_failed");

        var unknownStudent = await client.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = Guid.NewGuid(), subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        unknownStudent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(unknownStudent)).Should().Be("not_found");
    }

    [Fact]
    public async Task Teacher_lists_only_own_meetings()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var client2 = Client(app, seed.Teacher2UserId, seed.TenantId, "school.teacher");

        await client1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        await client2.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student2Id, subject = "Science", date = "2026-10-11", time = "11:30", mode = "Video call",
        });

        var res = await client1.GetAsync("/v1/ptm");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("teacher_id").GetGuid().Should().Be(seed.Teacher1Id);
    }

    [Fact]
    public async Task Admin_lists_all_and_filters_by_teacher()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var client2 = Client(app, seed.Teacher2UserId, seed.TenantId, "school.teacher");
        var admin = Client(app, seed.AdminUserId, seed.TenantId, "school.admin");

        await client1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        await client2.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student2Id, subject = "Science", date = "2026-10-11", time = "11:30", mode = "Video call",
        });

        var all = await admin.GetAsync("/v1/ptm");
        all.StatusCode.Should().Be(HttpStatusCode.OK);
        using var allDoc = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        allDoc.RootElement.GetProperty("data").EnumerateArray().Should().HaveCount(2);

        var filtered = await admin.GetAsync($"/v1/ptm?teacher_id={seed.Teacher1Id}");
        filtered.StatusCode.Should().Be(HttpStatusCode.OK);
        using var filteredDoc = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        var items = filteredDoc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("teacher_id").GetGuid().Should().Be(seed.Teacher1Id);
    }

    [Fact]
    public async Task Teacher_reschedule_resets_status()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var teacher1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var parent = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var create = await teacher1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var confirm = await parent.PatchAsJsonAsync($"/v1/ptm/{id}", new { status = "confirmed" });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        var reschedule = await teacher1.PatchAsJsonAsync($"/v1/ptm/{id}", new { time = "11:00" });
        reschedule.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rescheduleDoc = JsonDocument.Parse(await reschedule.Content.ReadAsStringAsync());
        rescheduleDoc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Teacher_cannot_edit_or_delete_other_teachers_meeting()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var teacher1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var teacher2 = Client(app, seed.Teacher2UserId, seed.TenantId, "school.teacher");

        var create = await teacher1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var patch = await teacher2.PatchAsJsonAsync($"/v1/ptm/{id}", new { time = "11:00" });
        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var delete = await teacher2.DeleteAsync($"/v1/ptm/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_deletes_meeting()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var teacher1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var admin = Client(app, seed.AdminUserId, seed.TenantId, "school.admin");

        var create = await teacher1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var delete = await admin.DeleteAsync($"/v1/ptm/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await admin.GetAsync("/v1/ptm");
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).Should().NotContain(id);
    }

    [Fact]
    public async Task Parent_cannot_create_or_delete()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var teacher1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var parent = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var create = await teacher1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var post = await parent.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-12", time = "10:30", mode = "Video call",
        });
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var delete = await parent.DeleteAsync($"/v1/ptm/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parent_list_still_has_original_keys()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var teacher1 = Client(app, seed.Teacher1UserId, seed.TenantId, "school.teacher");
        var parent = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        await teacher1.PostAsJsonAsync("/v1/ptm", new
        {
            student_id = seed.Student1Id, subject = "Maths", date = "2026-10-10", time = "10:30", mode = "Video call",
        });

        var res = await parent.GetAsync("/v1/ptm");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle();
        var m = items[0];
        foreach (var key in new[] { "id", "date", "time", "teacher", "subject", "child", "mode", "status" })
            m.TryGetProperty(key, out _).Should().BeTrue($"key '{key}' should be present");
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", jwt.IssueAccess(userId, tenantId, [role], isPlatform: false));
        return client;
    }

    private async Task<PtmStaffSeed> SeedAsync()
    {
        var s = new PtmStaffSeed(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var admission1 = $"PTMS/{s.Student1Id.ToString()[..8]}";
        var admission2 = $"PTMS/{s.Student2Id.ToString()[..8]}";
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId::text, false)", new { s.TenantId });
        await conn.ExecuteAsync(@"
INSERT INTO ""dbo"".""Users"" (""Id"", ""TenantId"", ""StudentId"", ""IsPlatform"", ""Status"") VALUES
    (@Teacher1UserId, @TenantId, NULL, false, 'active'),
    (@Teacher2UserId, @TenantId, NULL, false, 'active'),
    (@AdminUserId, @TenantId, NULL, false, 'active'),
    (@ParentUserId, @TenantId, NULL, false, 'active');
INSERT INTO ""dbo"".""Teachers"" (""Id"", ""TenantId"", ""Name"", ""UserId"") VALUES
    (@Teacher1Id, @TenantId, 'Mr. Teacher One', @Teacher1UserId),
    (@Teacher2Id, @TenantId, 'Ms. Teacher Two', @Teacher2UserId);
INSERT INTO ""dbo"".""Students"" (""Id"", ""TenantId"", ""AdmissionNo"", ""Name"", ""Status"") VALUES
    (@Student1Id, @TenantId, @admission1, 'Student One', 'active'),
    (@Student2Id, @TenantId, @admission2, 'Student Two', 'active');
INSERT INTO ""dbo"".""ParentStudentLinks"" (""ParentUserId"", ""StudentId"", ""TenantId"")
VALUES (@ParentUserId, @Student1Id, @TenantId);",
            new
            {
                s.TenantId, s.Teacher1UserId, s.Teacher2UserId, s.AdminUserId, s.ParentUserId,
                s.Teacher1Id, s.Teacher2Id, s.Student1Id, s.Student2Id, admission1, admission2,
            });
        return s;
    }

    private sealed record PtmStaffSeed(
        Guid TenantId, Guid Teacher1UserId, Guid Teacher2UserId, Guid AdminUserId, Guid ParentUserId,
        Guid Teacher1Id, Guid Teacher2Id, Guid Student1Id, Guid Student2Id, Guid Unused);
}
