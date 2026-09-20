using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class AssignmentTests(PostgresFixture fx)
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

    [Fact]
    public async Task Overdue_assignment_shows_correct_total_students_and_status()
    {
        // POST an assignment with a past due_date as teacher → 201
        // GET as teacher → total_students=2, submissions_count=0, status='overdue'
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        // Seed a class Grade 7, Section B
        var classRes = await Data(await teacher.PostAsJsonAsync("/v1/classes", new
        {
            name = "Class 7B",
            grade = "7",
            section = "B"
        }), HttpStatusCode.Created);
        var classId = classRes.GetProperty("id").GetGuid();

        // Seed 2 students in Grade 7 / Section B
        await Data(await teacher.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"S7B-{Guid.NewGuid():N}",
            name = "Alice",
            grade = "7",
            section = "B",
            roll = 1
        }), HttpStatusCode.Created);

        await Data(await teacher.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"S7B-{Guid.NewGuid():N}",
            name = "Bob",
            grade = "7",
            section = "B",
            roll = 2
        }), HttpStatusCode.Created);

        // POST an assignment for that class with a PAST due_date
        var pastDue = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");
        var created = await Data(await teacher.PostAsJsonAsync("/v1/assignments", new
        {
            title = "Past Assignment",
            class_id = classId,
            class_name = "Class 7B",
            subject = "Math",
            due_date = pastDue,
            description = "Old homework"
        }), HttpStatusCode.Created);

        created.GetProperty("title").GetString().Should().Be("Past Assignment");
        var assignmentId = created.GetProperty("id").GetGuid();

        // GET as teacher → assignment shows total_students=2, submissions_count=0, status='overdue'
        var list = await Data(await teacher.GetAsync("/v1/assignments"), HttpStatusCode.OK);
        list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        JsonElement? found = null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == assignmentId) { found = item; break; }
        }
        found.Should().NotBeNull("created assignment must appear in the teacher's list");
        found!.Value.GetProperty("status").GetString().Should().Be("overdue",
            "assignment with past due_date must be derived as 'overdue'");
        found.Value.GetProperty("total_students").GetInt32().Should().Be(2,
            "2 students are enrolled in Grade 7 / Section B");
        found.Value.GetProperty("submissions_count").GetInt32().Should().Be(0,
            "no homework submissions have been made");

        var admin = Client(app, tenantId, Policies.SchoolAdmin);
        var notifications = await Data(await admin.GetAsync("/v1/notifications"), HttpStatusCode.OK);
        notifications.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var hwNotice = notifications.EnumerateArray()
            .FirstOrDefault(n => n.GetProperty("title").GetString()?.Contains("Homework", StringComparison.OrdinalIgnoreCase) == true);
        hwNotice.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        hwNotice.GetProperty("title").GetString().Should().Contain("Past Assignment");
    }

    [Fact]
    public async Task Active_and_due_soon_assignments_derive_correct_status()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        // POST assignment due 10 days out → status active
        var futureDue = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        var activeCreated = await Data(await teacher.PostAsJsonAsync("/v1/assignments", new
        {
            title = "Active Assignment",
            due_date = futureDue
        }), HttpStatusCode.Created);
        var activeId = activeCreated.GetProperty("id").GetGuid();

        // POST assignment due tomorrow → status due_soon
        var tomorrowDue = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var dueSoonCreated = await Data(await teacher.PostAsJsonAsync("/v1/assignments", new
        {
            title = "Due Soon Assignment",
            due_date = tomorrowDue
        }), HttpStatusCode.Created);
        var dueSoonId = dueSoonCreated.GetProperty("id").GetGuid();

        var list = await Data(await teacher.GetAsync("/v1/assignments"), HttpStatusCode.OK);

        JsonElement? activeFound = null;
        JsonElement? dueSoonFound = null;
        foreach (var item in list.EnumerateArray())
        {
            var id = item.GetProperty("id").GetGuid();
            if (id == activeId) activeFound = item;
            if (id == dueSoonId) dueSoonFound = item;
        }

        activeFound.Should().NotBeNull("active assignment must appear in the list");
        activeFound!.Value.GetProperty("status").GetString().Should().Be("active",
            "assignment due 10 days out must be 'active'");

        dueSoonFound.Should().NotBeNull("due-soon assignment must appear in the list");
        dueSoonFound!.Value.GetProperty("status").GetString().Should().Be("due_soon",
            "assignment due tomorrow must be 'due_soon'");
    }

    [Fact]
    public async Task StudentOrParent_gets_403_on_get_and_post()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);

        var getRes = await student.GetAsync("/v1/assignments");
        getRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var postRes = await student.PostAsJsonAsync("/v1/assignments", new
        {
            title = "Should Fail"
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_and_update_persist_period_and_long_image()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var classRes = await Data(await teacher.PostAsJsonAsync("/v1/classes", new
        {
            name = "IV-B",
            grade = "IV",
            section = "B"
        }), HttpStatusCode.Created);
        var classId = classRes.GetProperty("id").GetGuid();

        var image = "data:image/jpeg;base64," + new string('A', 500);
        var created = await Data(await teacher.PostAsJsonAsync("/v1/assignments", new
        {
            title = "Music practice",
            class_id = classId,
            class_name = "IV-B",
            subject = "Music",
            period = 8,
            due_date = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd"),
            image_uri = image
        }), HttpStatusCode.Created);

        created.GetProperty("period").GetInt32().Should().Be(8);
        created.GetProperty("image_uri").GetString().Should().Be(image);
        created.GetProperty("subject").GetString().Should().Be("Music");
        var id = created.GetProperty("id").GetGuid();

        var updatedImage = "data:image/png;base64," + new string('B', 500);
        var updated = await Data(await teacher.PatchAsJsonAsync($"/v1/assignments/{id}", new
        {
            title = "Music practice 2",
            class_id = classId,
            class_name = "IV-B",
            subject = "Music",
            period = 8,
            due_date = DateTime.UtcNow.AddDays(6).ToString("yyyy-MM-dd"),
            image_uri = updatedImage
        }), HttpStatusCode.OK);

        updated.GetProperty("title").GetString().Should().Be("Music practice 2");
        updated.GetProperty("image_uri").GetString().Should().Be(updatedImage);
        updated.GetProperty("period").GetInt32().Should().Be(8);
    }
}
