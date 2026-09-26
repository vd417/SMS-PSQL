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

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """INSERT INTO "dbo"."Tenants" ("Id","Name","Slug","Status") VALUES (@id,'Not Seed',@slug,'active') ON CONFLICT DO NOTHING""", conn);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("slug", shadowSlug);
            await cmd.ExecuteNonQueryAsync();
        }

        var act = () => SeedRunner.RunAsync(fx.ConnectionString, rows);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shadowed*");
        await using var check = new NpgsqlConnection(fx.ConnectionString);
        await check.OpenAsync();
        await using var q = new NpgsqlCommand("""SELECT count(*) FROM "dbo"."Tenants" WHERE "Id" = @id""", check);
        q.Parameters.AddWithValue("id", probeId);
        ((long)(await q.ExecuteScalarAsync())!).Should().Be(0);
    }
}
