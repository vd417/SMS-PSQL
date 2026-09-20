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
        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@userId, @tenantId)", new { userId, tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        await client.GetAsync("/v1/auth/me");

        await using var checkConn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await checkConn.OpenAsync();
        await checkConn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        var lastSeen = await checkConn.QuerySingleAsync<DateTime?>(
            "SELECT LastSeenAt FROM dbo.Users WHERE Id = @userId", new { userId });
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
        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name, LastSeenAt) VALUES (@userId, @tenantId, 'Chat Contact', SYSUTCDATETIME())",
                new { userId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ChatThreads (TenantId, OwnerUserId, Name) VALUES (@tenantId, @userId, 'Chat Contact')",
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

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@teacherUserId, @tenantId, 'Ms. Teacher')",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@parentUserId, @tenantId, 'Parent Contact')",
                new { parentUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, ClassLabel) " +
                "VALUES (@studentId, @tenantId, 'A1', 'Kid Rahul', 'Grade 5 - A')",
                new { studentId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@parentUserId, @studentId, @tenantId)",
                new { parentUserId, studentId, tenantId });
            // The parent's own thread with the teacher, scoped to their child — mirrors what
            // the parent app creates when messaging about a specific student.
            await conn.ExecuteAsync(
                "INSERT dbo.ChatThreads (Id, TenantId, OwnerUserId, Name, ContactUserId, ChildId) " +
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
}
