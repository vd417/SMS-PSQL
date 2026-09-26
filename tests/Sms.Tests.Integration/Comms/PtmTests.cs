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
public class PtmTests(PostgresFixture fx)
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
    public async Task Parent_lists_only_linked_childrens_meetings()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var res = await client.GetAsync("/v1/ptm");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle();
        var m = items[0];
        m.GetProperty("id").GetGuid().Should().Be(seed.LinkedMeetingId);
        m.GetProperty("child").GetGuid().Should().Be(seed.LinkedStudentId);
        m.GetProperty("teacher").GetString().Should().Be("Ms. A. Krishnan");
        m.GetProperty("subject").GetString().Should().Be("Mathematics");
        m.GetProperty("date").GetString().Should().Be("2026-10-03");
        m.GetProperty("time").GetString().Should().Be("15:00");
        m.GetProperty("mode").GetString().Should().Be("Video call");
        m.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Student_lists_own_meetings()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.StudentUserId, seed.TenantId, "student");

        var res = await client.GetAsync("/v1/ptm");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).Should().Equal(seed.LinkedMeetingId);
    }

    [Fact]
    public async Task Parent_confirms_linked_meeting()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var res = await client.PatchAsJsonAsync($"/v1/ptm/{seed.LinkedMeetingId}", new { status = "confirmed" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("confirmed");
        (await StatusOfAsync(seed.TenantId, seed.LinkedMeetingId)).Should().Be("confirmed");
    }

    [Fact]
    public async Task Parent_cannot_update_unlinked_meeting()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var res = await client.PatchAsJsonAsync($"/v1/ptm/{seed.OtherMeetingId}", new { status = "confirmed" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(res)).Should().Be("not_found");
        (await StatusOfAsync(seed.TenantId, seed.OtherMeetingId)).Should().Be("pending");
    }

    [Fact]
    public async Task Invalid_status_is_rejected()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.ParentUserId, seed.TenantId, "parent");

        var res = await client.PatchAsJsonAsync($"/v1/ptm/{seed.LinkedMeetingId}", new { status = "cancelled" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(res)).Should().Be("invalid_status");
    }

    [Fact]
    public async Task Student_cannot_update_meeting()
    {
        await using var app = App();
        var seed = await SeedAsync();
        var client = Client(app, seed.StudentUserId, seed.TenantId, "student");

        var res = await client.PatchAsJsonAsync($"/v1/ptm/{seed.LinkedMeetingId}", new { status = "confirmed" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusOfAsync(seed.TenantId, seed.LinkedMeetingId)).Should().Be("pending");
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private async Task<string> StatusOfAsync(Guid tenantId, Guid meetingId)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
        return await conn.ExecuteScalarAsync<string>(
            "SELECT \"Status\" FROM \"dbo\".\"PtmMeetings\" WHERE \"Id\" = @meetingId", new { meetingId }) ?? "";
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

    private async Task<PtmSeed> SeedAsync()
    {
        var s = new PtmSeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var admissionNo = $"PTM/{s.LinkedStudentId.ToString()[..8]}";
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @TenantId::text, false)", new { s.TenantId });
        await conn.ExecuteAsync(@"
INSERT INTO ""dbo"".""Users"" (""Id"", ""TenantId"", ""StudentId"", ""IsPlatform"", ""Status"") VALUES
    (@ParentUserId, @TenantId, NULL, false, 'active'),
    (@StudentUserId, @TenantId, @admissionNo, false, 'active');
INSERT INTO ""dbo"".""Students"" (""Id"", ""TenantId"", ""AdmissionNo"", ""Name"", ""Status"") VALUES
    (@LinkedStudentId, @TenantId, @admissionNo, 'Linked Student', 'active'),
    (@OtherStudentId, @TenantId, @otherAdmissionNo, 'Other Student', 'active');
INSERT INTO ""dbo"".""ParentStudentLinks"" (""ParentUserId"", ""StudentId"", ""TenantId"")
VALUES (@ParentUserId, @LinkedStudentId, @TenantId);
INSERT INTO ""dbo"".""Teachers"" (""Id"", ""TenantId"", ""Name"") VALUES (@TeacherId, @TenantId, 'Ms. A. Krishnan');
INSERT INTO ""dbo"".""PtmMeetings""
    (""Id"", ""TenantId"", ""StudentId"", ""TeacherId"", ""Subject"", ""MeetingDate"", ""MeetingTime"", ""Mode"") VALUES
    (@LinkedMeetingId, @TenantId, @LinkedStudentId, @TeacherId, 'Mathematics', '2026-10-03', '15:00', 'Video call'),
    (@OtherMeetingId, @TenantId, @OtherStudentId, @TeacherId, 'Physics', '2026-10-04', '09:30', 'In-person · Room C-214');",
            new
            {
                s.TenantId, s.ParentUserId, s.StudentUserId, s.LinkedStudentId, s.OtherStudentId, s.TeacherId,
                s.LinkedMeetingId, s.OtherMeetingId, admissionNo,
                otherAdmissionNo = $"PTM/{s.OtherStudentId.ToString()[..8]}",
            });
        return s;
    }

    private sealed record PtmSeed(
        Guid TenantId, Guid ParentUserId, Guid StudentUserId, Guid LinkedStudentId, Guid OtherStudentId,
        Guid TeacherId, Guid LinkedMeetingId, Guid OtherMeetingId);
}
