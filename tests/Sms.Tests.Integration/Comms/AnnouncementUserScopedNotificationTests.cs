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
public class AnnouncementUserScopedNotificationTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    [Fact]
    public async Task Announcement_with_a_user_id_targets_only_that_user_not_the_whole_tenant()
    {
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var guardianUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES " +
                "(@principalUserId, @tenantId, 'Priya Principal'), " +
                "(@guardianUserId, @tenantId, 'Guardian Of Aarav'), " +
                "(@otherUserId, @tenantId, 'Some Other Parent')",
                new { principalUserId, guardianUserId, otherUserId, tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());

        HttpClient ClientFor(Guid userId, string role)
        {
            var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            return client;
        }

        var principalClient = ClientFor(principalUserId, Policies.Principal);

        var create = await principalClient.PostAsJsonAsync("/v1/announcements", new
        {
            title = "Fee invoice generated",
            body = "A Term 2 fee invoice of 8500 has been generated for Aarav. Due date: 2026-03-15.",
            type = "fee_invoice",
            audience = "specific",
            channels = new[] { "app" },
            user_id = guardianUserId,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var guardianClient = ClientFor(guardianUserId, "school.parent");
        var otherClient = ClientFor(otherUserId, "school.parent");

        var guardianList = await guardianClient.GetAsync("/v1/notifications");
        guardianList.StatusCode.Should().Be(HttpStatusCode.OK);
        using var guardianDoc = JsonDocument.Parse(await guardianList.Content.ReadAsStringAsync());
        var guardianTitles = guardianDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()).ToList();
        guardianTitles.Should().Contain(t => t != null && t.Contains("Fee invoice generated"));

        var otherList = await otherClient.GetAsync("/v1/notifications");
        otherList.StatusCode.Should().Be(HttpStatusCode.OK);
        using var otherDoc = JsonDocument.Parse(await otherList.Content.ReadAsStringAsync());
        var otherTitles = otherDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()).ToList();
        otherTitles.Should().NotContain(t => t != null && t.Contains("Fee invoice generated"));
    }
}
