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

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class BusTravelingTeacherTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.Principal], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient TeacherClient(WebApplicationFactory<Program> app, Guid tenantId, Guid teacherUserId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(teacherUserId, tenantId, [Policies.Teacher], isPlatform: false);
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

    [Fact]
    public async Task Admin_can_add_and_remove_multiple_traveling_teachers_on_the_same_bus()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacher1 = Guid.NewGuid();
        var teacher2 = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Users\" (\"Id\", \"TenantId\", \"Name\", \"Email\") VALUES (@Id, @TenantId, @Name, @Email)",
                new[]
                {
                    new { Id = teacher1, TenantId = tenantId, Name = "Asha Rao", Email = $"asha-{teacher1}@test.local" },
                    new { Id = teacher2, TenantId = tenantId, Name = "Bala Iyer", Email = $"bala-{teacher2}@test.local" },
                });
        }

        var admin = PrincipalClient(app, tenantId);

        var afterFirst = await Data(
            await admin.PutAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher1}", null), HttpStatusCode.OK);
        afterFirst.GetArrayLength().Should().Be(1);

        var afterSecond = await Data(
            await admin.PutAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher2}", null), HttpStatusCode.OK);
        afterSecond.GetArrayLength().Should().Be(2);

        var list = await Data(await admin.GetAsync($"/v1/transport/buses/{busId}/traveling-teachers"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("teacher_name").GetString())
            .Should().BeEquivalentTo(["Asha Rao", "Bala Iyer"]);

        var afterRemove = await admin.DeleteAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher1}");
        afterRemove.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var remaining = await Data(await admin.GetAsync($"/v1/transport/buses/{busId}/traveling-teachers"), HttpStatusCode.OK);
        remaining.GetArrayLength().Should().Be(1);
        remaining[0].GetProperty("teacher_name").GetString().Should().Be("Bala Iyer");
    }

    [Fact]
    public async Task Teacher_sees_every_bus_they_are_mapped_as_a_traveling_teacher_on_via_bus_traveling()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();

        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
        }

        var admin = PrincipalClient(app, tenantId);
        await admin.PutAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacherId}", null);

        var teacher = TeacherClient(app, tenantId, teacherId);
        var data = await Data(await teacher.GetAsync("/v1/bus/traveling"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("bus_no").GetString().Should().Be("BUS-1");
    }
}
