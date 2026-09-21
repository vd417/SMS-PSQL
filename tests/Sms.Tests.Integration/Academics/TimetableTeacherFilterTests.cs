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

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class TimetableTeacherFilterTests(PostgresFixture fx)
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
    public async Task Teacher_only_sees_their_own_class_slots_not_the_whole_tenant()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId)", new { teacherUserId, tenantId });
            var teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, 'T1', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            var myClassId = Guid.NewGuid();
            var otherClassId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId) VALUES (@myClassId, @tenantId, 'MyClass', 0, @teacherId)",
                new { myClassId, tenantId, teacherId });
            await conn.ExecuteAsync(
                "INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId) VALUES (@otherClassId, @tenantId, 'OtherClass', 0, NULL)",
                new { otherClassId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, ClassId, ClassName) VALUES (@tenantId, 'Mon', 1, @myClassId, 'MyClass')",
                new { tenantId, myClassId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, ClassId, ClassName) VALUES (@tenantId, 'Mon', 2, @otherClassId, 'OtherClass')",
                new { tenantId, otherClassId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(teacherUserId, tenantId, new[] { Policies.Teacher }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/timetable");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var slots = doc.RootElement.GetProperty("data");
        slots.GetArrayLength().Should().Be(1);
        slots[0].GetProperty("class_name").GetString().Should().Be("MyClass");
    }

    [Fact]
    public async Task Principal_sees_the_whole_tenant_grid_unfiltered()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, ClassName) VALUES (@tenantId, 'Tue', 1, 'X')",
                new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, ClassName) VALUES (@tenantId, 'Tue', 2, 'Y')",
                new { tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/timetable");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Admin_sees_the_whole_tenant_grid_unfiltered()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassName) VALUES (@tenantId, 'Mon', 1, 'Math', 'IV-B')",
                new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassName) VALUES (@tenantId, 'Tue', 2, 'Hindi', 'V-A')",
                new { tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, new[] { Policies.SchoolAdmin }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var res = await client.GetAsync("/v1/timetable");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }
}
