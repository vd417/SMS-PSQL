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

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class ChatPresenceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Authenticated_request_touches_LastSeenAt()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\") VALUES (@userId, @tenantId)", new { userId, tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        await client.GetAsync("/v1/auth/me");

        await using var checkConn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await checkConn.OpenAsync();
        await checkConn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        var lastSeen = await checkConn.QuerySingleAsync<DateTime?>(
            "SELECT \"LastSeenAt\" FROM \"dbo\".\"Users\" WHERE \"Id\" = @userId", new { userId });
        lastSeen.Should().NotBeNull();
        lastSeen!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Chat_thread_shows_online_when_matched_user_recently_seen()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\", \"LastSeenAt\") VALUES (@userId, @tenantId, 'Chat Contact', now() AT TIME ZONE 'UTC')",
                new { userId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ChatThreads\" (\"TenantId\", \"OwnerUserId\", \"Name\") VALUES (@tenantId, @userId, 'Chat Contact')",
                new { tenantId, userId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/threads");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        var found = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("name").GetString() == "Chat Contact")
            {
                row.GetProperty("online").GetBoolean().Should().BeTrue();
                found = true;
            }
        }
        found.Should().BeTrue();
    }

    [Fact]
    public async Task Parent_reply_delivers_to_teacher_inbox_labeled_parent_with_child_context()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var parentThreadId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@teacherUserId, @tenantId, 'Ms. Teacher')",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@parentUserId, @tenantId, 'Parent Contact')",
                new { parentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"ClassLabel\") " +
                "VALUES (@studentId, @tenantId, 'A1', 'Kid Rahul', 'Grade 5 - A')",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ParentStudentLinks\" (\"ParentUserId\", \"StudentId\", \"TenantId\") VALUES (@parentUserId, @studentId, @tenantId)",
                new { parentUserId, studentId, tenantId });
            // The parent's own thread with the teacher, scoped to their child — mirrors what
            // the parent app creates when messaging about a specific student.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ChatThreads\" (\"Id\", \"TenantId\", \"OwnerUserId\", \"Name\", \"ContactUserId\", \"ChildId\") " +
                "VALUES (@parentThreadId, @tenantId, @parentUserId, 'Ms. Teacher', @teacherUserId, @studentId)",
                new { parentThreadId, tenantId, parentUserId, teacherUserId, studentId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());

        var parentClient = app.CreateClient();
        parentClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(parentUserId, tenantId, new[] { Policies.StudentOrParent }, isPlatform: false));
        var sendRes = await parentClient.PostAsJsonAsync(
            $"/v1/threads/{parentThreadId}/messages", new { text = "Hello teacher" });
        sendRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var teacherClient = app.CreateClient();
        teacherClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(teacherUserId, tenantId, new[] { Policies.Teacher }, isPlatform: false));
        var listRes = await teacherClient.GetAsync("/v1/threads");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        var found = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("name").GetString() != "Parent Contact") continue;
            found = true;
            // The bug: this used to always resolve to "Teacher" for any sender that wasn't
            // in the Teachers/Staff tables, mislabeling every parent reply.
            row.GetProperty("role").GetString().Should().Be("Parent");
            row.GetProperty("child_name").GetString().Should().Be("Kid Rahul");
            row.GetProperty("child_class_label").GetString().Should().Be("Grade 5 - A");
        }
        found.Should().BeTrue("the parent's reply should have created a mirrored thread in the teacher's inbox");
    }

    [Fact]
    public async Task Staff_message_to_a_Student_thread_without_ContactUserId_delivers_to_the_parent()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var staffUserId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var staffThreadId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@staffUserId, @tenantId, 'Front Office')",
                new { staffUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@parentUserId, @tenantId, 'Parent Of Arav')",
                new { parentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"ClassLabel\") " +
                "VALUES (@studentId, @tenantId, 'A2', 'Arav Sharma', 'I-A')",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ParentStudentLinks\" (\"ParentUserId\", \"StudentId\", \"TenantId\") VALUES (@parentUserId, @studentId, @tenantId)",
                new { parentUserId, studentId, tenantId });
            // The exact shape that failed in prod: a Student-role thread created with only a name,
            // so ContactUserId and ChildId are both NULL. Delivery must still find the parent.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ChatThreads\" (\"Id\", \"TenantId\", \"OwnerUserId\", \"Name\", \"Role\") " +
                "VALUES (@staffThreadId, @tenantId, @staffUserId, 'Arav Sharma', 'Student')",
                new { staffThreadId, tenantId, staffUserId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());

        var staffClient = app.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(staffUserId, tenantId, new[] { Policies.SchoolAdmin }, isPlatform: false));
        var sendRes = await staffClient.PostAsJsonAsync(
            $"/v1/threads/{staffThreadId}/messages", new { text = "gi" });
        sendRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var parentClient = app.CreateClient();
        parentClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(parentUserId, tenantId, new[] { Policies.StudentOrParent }, isPlatform: false));
        var listRes = await parentClient.GetAsync("/v1/threads");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("data");
        var delivered = false;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.GetProperty("name").GetString() != "Front Office") continue;
            delivered = true;
            row.GetProperty("last_message").GetString().Should().Be("gi");
        }
        delivered.Should().BeTrue("a Student-role thread with no ContactUserId must still deliver to the linked parent");
    }

    [Fact]
    public async Task Student_senders_thread_shows_their_name_and_class_roll_not_School_Office()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        var studentUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var studentThreadId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\") VALUES (@teacherUserId, @tenantId, 'Ms Teacher')",
                new { teacherUserId, tenantId });
            // Student's own login: no Users.Name, StudentId links to the roster by AdmissionNo.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"StudentId\") VALUES (@studentUserId, @tenantId, 'STU-ROLL-1')",
                new { studentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"ClassLabel\", \"Roll\") " +
                "VALUES (@studentId, @tenantId, 'STU-ROLL-1', 'Arav Sharma', 'I-A', 1)",
                new { studentId, tenantId });
            // Thread the student owns, addressed to the teacher by ContactUserId.
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"ChatThreads\" (\"Id\", \"TenantId\", \"OwnerUserId\", \"Name\", \"Role\", \"ContactUserId\") " +
                "VALUES (@studentThreadId, @tenantId, @studentUserId, 'Ms Teacher', 'Teacher', @teacherUserId)",
                new { studentThreadId, tenantId, studentUserId, teacherUserId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());

        var studentClient = app.CreateClient();
        studentClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(studentUserId, tenantId, new[] { Policies.StudentOrParent }, isPlatform: false));
        (await studentClient.PostAsJsonAsync($"/v1/threads/{studentThreadId}/messages", new { text = "hi sir" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var teacherClient = app.CreateClient();
        teacherClient.DefaultRequestHeaders.Authorization = new(
            "Bearer", jwt.IssueAccess(teacherUserId, tenantId, new[] { Policies.Teacher }, isPlatform: false));
        var listRes = await teacherClient.GetAsync("/v1/threads");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await listRes.Content.ReadAsStringAsync());
        var found = false;
        foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (row.GetProperty("last_message").GetString() != "hi sir") continue;
            found = true;
            row.GetProperty("name").GetString().Should().Be("Arav Sharma",
                "a student sender must show their real name, never the 'School Office' fallback");
            row.GetProperty("child_class_label").GetString().Should().Be("I-A");
            row.GetProperty("child_roll").GetInt32().Should().Be(1);
        }
        found.Should().BeTrue("the student's message must create a mirrored thread in the teacher's inbox");
    }
}
