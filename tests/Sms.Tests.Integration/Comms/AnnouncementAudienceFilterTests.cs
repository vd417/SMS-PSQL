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

/// <summary>
/// Finding #2 from the live contract check: the student/parent app calls
/// GET /v1/announcements?audience=student|parent (singular), but stored audiences use
/// the admin's vocabulary (parents/students/teachers/staff/everyone/all/grades/defaulters/specific).
/// The filter must match school-wide + singular-or-plural of the request, but never leak
/// targeted deliveries (grades/defaulters/specific) into a generic audience query.
/// </summary>
[Collection("sql")]
public class AnnouncementAudienceFilterTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task SeedAnnouncementAsync(
        string connectionString, Guid tenantId, string title, string? audience)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."Announcements" ("Id", "TenantId", "Title", "Body", "Type", "Audience")
            VALUES (@id, @tenantId, @title, 'body', 'info', @audience)
            """,
            new { id = Guid.NewGuid(), tenantId, title, audience });
    }

    [Fact]
    public async Task Parent_query_sees_school_wide_and_plural_parents_not_students_or_defaulters()
    {
        var tenantId = Guid.NewGuid();
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "All-school notice", "all");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Everyone notice", "everyone");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Parents notice", "parents");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Students notice", "students");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Defaulters notice", "defaulters");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "No audience notice", null);

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.StudentOrParent], isPlatform: false);

        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var list = await Data(await client.GetAsync("/v1/announcements?audience=parent"), HttpStatusCode.OK);
        var titles = list.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();

        titles.Should().Contain(new[]
        {
            "All-school notice", "Everyone notice", "Parents notice", "No audience notice",
        });
        titles.Should().NotContain(new[] { "Students notice", "Defaulters notice" });
    }

    [Fact]
    public async Task Student_query_sees_school_wide_and_plural_students_not_parents_or_defaulters()
    {
        var tenantId = Guid.NewGuid();
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "All-school notice", "all");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Everyone notice", "everyone");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Parents notice", "parents");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Students notice", "students");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "Defaulters notice", "defaulters");
        await SeedAnnouncementAsync(fx.ConnectionString, tenantId, "No audience notice", null);

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.StudentOrParent], isPlatform: false);

        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var list = await Data(await client.GetAsync("/v1/announcements?audience=student"), HttpStatusCode.OK);
        var titles = list.EnumerateArray().Select(e => e.GetProperty("title").GetString()).ToList();

        titles.Should().Contain(new[]
        {
            "All-school notice", "Everyone notice", "Students notice", "No audience notice",
        });
        titles.Should().NotContain(new[] { "Parents notice", "Defaulters notice" });
    }
}
