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

    private async Task<(Guid BusId, Guid RouteId, Guid Stop1, Guid Stop2, Guid Teacher)> SeedBusWithTwoStops(Guid tenantId)
    {
        var busId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stop1 = Guid.NewGuid();
        var stop2 = Guid.NewGuid();
        var teacher = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Buses\" (\"Id\",\"TenantId\",\"BusNo\",\"RouteId\") VALUES (@Id,@T,'BUS-9',@R)",
            new { Id = busId, T = tenantId, R = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"TransportRoutes\" (\"Id\",\"TenantId\",\"Name\") VALUES (@R,@T,'R9')",
            new { R = routeId, T = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO \"dbo\".\"RouteStops\" (\"Id\",\"TenantId\",\"RouteId\",\"Name\",\"Seq\",\"Lat\",\"Lng\") VALUES (@S1,@T,@R,'Gate A',1,12.90,77.60),(@S2,@T,@R,'Gate B',2,12.91,77.60)",
            new { S1 = stop1, S2 = stop2, T = tenantId, R = routeId });
        await conn.ExecuteAsync("INSERT INTO \"dbo\".\"Users\" (\"Id\",\"TenantId\",\"Name\",\"Email\") VALUES (@Id,@T,'Chitra',@E)",
            new { Id = teacher, T = tenantId, E = $"chitra-{teacher}@test.local" });
        return (busId, routeId, stop1, stop2, teacher);
    }

    [Fact]
    public async Task Admin_assigns_a_traveling_teacher_with_a_stop_and_can_update_it()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, _, stop1, stop2, teacher) = await SeedBusWithTwoStops(tenantId);
        var admin = PrincipalClient(app, tenantId);

        var afterAssign = await Data(
            await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher}", new { stop_id = stop1 }),
            HttpStatusCode.OK);
        var row = afterAssign.EnumerateArray().Single(e => e.GetProperty("teacher_user_id").GetGuid() == teacher);
        row.GetProperty("stop_id").GetGuid().Should().Be(stop1);
        row.GetProperty("stop_name").GetString().Should().Be("Gate A");

        // Re-assigning the same teacher updates the stop in place (upsert — still one row).
        var afterUpdate = await Data(
            await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher}", new { stop_id = stop2 }),
            HttpStatusCode.OK);
        afterUpdate.GetArrayLength().Should().Be(1);
        afterUpdate.EnumerateArray().Single().GetProperty("stop_name").GetString().Should().Be("Gate B");
    }

    [Fact]
    public async Task Assigning_a_traveling_teacher_without_a_stop_leaves_the_stop_null()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var (busId, _, _, _, teacher) = await SeedBusWithTwoStops(tenantId);
        var admin = PrincipalClient(app, tenantId);

        var after = await Data(
            await admin.PutAsync($"/v1/transport/buses/{busId}/traveling-teachers/{teacher}", null),
            HttpStatusCode.OK);
        var row = after.EnumerateArray().Single(e => e.GetProperty("teacher_user_id").GetGuid() == teacher);
        row.GetProperty("stop_id").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
