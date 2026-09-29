using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.DevSeed;
using Sms.Shared.Kernel.Auth;
using Xunit;

namespace Sms.Tests.Integration.DevSeed;

[Collection("sql")]
public class DevSeedTests(PostgresFixture fx)
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
    public async Task Seed_is_idempotent_and_does_not_alter_seeded_rows()
    {
        var rows = SeedData.Build(new PasswordHasher());
        await SeedRunner.RunAsync(fx.ConnectionString, rows);
        var afterFirst = await SeedChecksum.ComputeAsync(fx.ConnectionString);

        var second = await SeedRunner.RunAsync(fx.ConnectionString, SeedData.Build(new PasswordHasher()));
        var afterSecond = await SeedChecksum.ComputeAsync(fx.ConnectionString);

        second.TotalInserted.Should().Be(0);
        afterSecond.Should().BeEquivalentTo(afterFirst);
        foreach (var table in SeedData.Tables)
            afterFirst[table].Count.Should().Be(rows.Count(r => r.Table == table), $"table {table}");
    }

    [Fact]
    public async Task Seeded_teacher_logs_in_through_the_real_auth_endpoint()
    {
        await SeedRunner.RunAsync(fx.ConnectionString, SeedData.Build(new PasswordHasher()));
        using var app = App();
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = SeedData.TeacherAEmail, password = SeedData.TeacherPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("access_token").GetString();

        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        var me = (await client.GetFromJsonAsync<JsonElement>("/v1/auth/me")).GetProperty("data");
        me.GetProperty("tenant_id").GetGuid().Should().Be(SeedData.MainTenantId);
        me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Contain("school.teacher");
        me.GetProperty("employee").GetString().Should().Be("DS-T001");
        me.GetProperty("tier").GetString().Should().Be("platinum");
    }

    [Fact]
    public async Task Seed_fails_and_commits_nothing_when_a_seed_row_is_shadowed()
    {
        // A non-seed tenant already owns the slug a seed (shadowed) tenant wants, so ON CONFLICT DO
        // NOTHING would silently skip it. The runner must detect that and roll everything back —
        // including the probe row, which has no conflict of its own and would otherwise insert cleanly.
        // That is what proves rollback rather than merely proving the shadowed row itself never lands
        // (it never would, conflict or not).
        var probeId = SeedIds.Of($"probe.{Guid.NewGuid()}");
        var probeSlug = $"commit-nothing-probe-{Guid.NewGuid():N}";
        var shadowSlug = $"commit-nothing-shadow-{Guid.NewGuid():N}";
        var shadowedId = SeedIds.Of("shadow.tenant");

        var rows = new List<SeedRow>
        {
            new("Tenants", new Dictionary<string, object>
                { ["Id"] = probeId, ["Name"] = "Probe", ["Slug"] = probeSlug, ["Status"] = "active" }),
            new("Tenants", new Dictionary<string, object>
                { ["Id"] = shadowedId, ["Name"] = "Shadowed", ["Slug"] = shadowSlug, ["Status"] = "active" }),
        };

        var notSeedId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """INSERT INTO "dbo"."Tenants" ("Id","Name","Slug","Status") VALUES (@id,'Not Seed',@slug,'active') ON CONFLICT DO NOTHING""", conn);
            cmd.Parameters.AddWithValue("id", notSeedId);
            cmd.Parameters.AddWithValue("slug", shadowSlug);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var act = () => SeedRunner.RunAsync(fx.ConnectionString, rows);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shadowed*");
            await using var check = new NpgsqlConnection(fx.ConnectionString);
            await check.OpenAsync();
            await using var q = new NpgsqlCommand("""SELECT count(*) FROM "dbo"."Tenants" WHERE "Id" = @id""", check);
            q.Parameters.AddWithValue("id", probeId);
            ((long)(await q.ExecuteScalarAsync())!).Should().Be(0);
        }
        finally
        {
            // The shadowing "Not Seed" row is inserted outside the runner's transaction, so it survives
            // the rollback; remove it so it does not accumulate in the shared test database.
            await using var cleanup = new NpgsqlConnection(fx.ConnectionString);
            await cleanup.OpenAsync();
            await using var del = new NpgsqlCommand("""DELETE FROM "dbo"."Tenants" WHERE "Id" = @id""", cleanup);
            del.Parameters.AddWithValue("id", notSeedId);
            await del.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task B6_rider_stop_ids_are_route_stop_ids()
    {
        await SeedRunner.RunAsync(fx.ConnectionString, SeedData.Build(new PasswordHasher()));

        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using (var tenantCmd = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant::text, false)", conn))
        {
            tenantCmd.Parameters.AddWithValue("tenant", SeedData.MainTenantId.ToString());
            await tenantCmd.ExecuteNonQueryAsync();
        }
        await using var cmd = new NpgsqlCommand(
            """SELECT "StopId" FROM "dbo"."StudentBusAssignments" WHERE "TenantId" = @tenant""", conn);
        cmd.Parameters.AddWithValue("tenant", SeedData.MainTenantId);
        var stopIds = new List<Guid>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                stopIds.Add(reader.GetGuid(0));
        stopIds.Should().HaveCount(5);

        await using var routeStopCmd = new NpgsqlCommand(
            """SELECT "Id" FROM "dbo"."RouteStops" WHERE "TenantId" = @tenant""", conn);
        routeStopCmd.Parameters.AddWithValue("tenant", SeedData.MainTenantId);
        var routeStopIds = new List<Guid>();
        await using (var reader = await routeStopCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                routeStopIds.Add(reader.GetGuid(0));
        routeStopIds.Should().HaveCount(3);

        stopIds.Should().OnlyContain(id => routeStopIds.Contains(id));
    }

    [Fact]
    public async Task NEW2_multi_teacher_sees_other_school_class_after_switch()
    {
        await SeedRunner.RunAsync(fx.ConnectionString, SeedData.Build(new PasswordHasher()));
        using var app = App();
        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync("/v1/auth/login",
            new { email = SeedData.MultiEmail, password = SeedData.TeacherPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginData = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var access = loginData.GetProperty("access_token").GetString();

        client.DefaultRequestHeaders.Authorization = new("Bearer", access);
        var me = (await client.GetFromJsonAsync<JsonElement>("/v1/auth/me")).GetProperty("data");
        var currentTenant = me.GetProperty("tenant_id").GetGuid();
        var otherTenant = currentTenant == SeedData.MainTenantId ? SeedData.OtherTenantId : SeedData.MainTenantId;

        var switchResp = await client.PostAsJsonAsync("/v1/me/switch-school", new { tenant_id = otherTenant });
        switchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var switchData = (await switchResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var newAccess = switchData.GetProperty("access_token").GetString();

        client.DefaultRequestHeaders.Authorization = new("Bearer", newAccess);
        var classesResp = await client.GetAsync("/v1/classes");
        classesResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var classes = (await classesResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var names = classes.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        names.Should().ContainSingle().Which.Should().Be("IX-A");
    }
}
