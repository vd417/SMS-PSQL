using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Tests.Integration.Catre;

/// DELETE /v1/clients/{id} end to end: runs the real dbo.client_delete Postgres function (via
/// ClientRepository.DeleteEmptyAsync) as the non-superuser sms_app role, so a parameter-name
/// mismatch between the C# call and the function signature, or an RLS policy blocking one of its
/// cascading DELETEs, fails these tests.
[Collection("sql")]
public class ClientDeleteTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PlatformClient(WebApplicationFactory<Program> app)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), null, ["owner"], isPlatform: true);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid id) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/v1/clients/{id}")
        {
            Content = JsonContent.Create(new { confirm = "DELETE" }),
        });

    private static async Task<Guid> CreateActiveClientAsync(HttpClient client)
    {
        var plan = await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Gold", tier = "gold", pricing = "flat", price = 14999m, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 1200, staff = 120, storage_gb = 50 },
            visibility = "published", audience = "all"
        });
        plan.StatusCode.Should().Be(HttpStatusCode.Created);
        var planId = (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();

        var created = await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Delete Me High", slug = $"delete-me-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "Asha Rao", admin_email = $"admin-{Guid.NewGuid():N}@example.test", plan_id = planId, trial_days = 14
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetGuid();

        // Activating creates the subscription/invoice rows, so the delete has to cascade through them.
        (await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private async Task<long> CountAsync(string sql, Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        await using var conn = await new NpgsqlConnectionFactory(fx.ConnectionString, ctx).OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql, new { tenantId });
    }

    // Tables the baseline port of Client_Delete forgot to clear (compared with the source proc,
    // db/Sms.Migrations/procs/catredel/Client_Delete.sql). Migration 0001 restores them.
    private static readonly string[] RestoredDependents =
        ["UserAppSettings", "PeriodAttendanceRecords", "PeriodAttendanceAudit", "Achievements"];

    private async Task SeedRestoredDependentsAsync(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, Guid.NewGuid(), false);
        await using var conn = await new NpgsqlConnectionFactory(fx.ConnectionString, ctx).OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO "dbo"."UserAppSettings" ("UserId", "TenantId") VALUES (@u, @t);
            INSERT INTO "dbo"."PeriodAttendanceRecords" ("Id", "TenantId", "ClassId", "StudentId", "Date", "Period", "Subject", "Status")
                VALUES (@r, @t, @c, @s, DATE '2026-09-01', 1, 'Maths', 'present');
            INSERT INTO "dbo"."PeriodAttendanceAudit" ("TenantId", "RecordId", "ClassId", "StudentId", "Date", "Period", "Subject", "ToStatus")
                VALUES (@t, @r, @c, @s, DATE '2026-09-01', 1, 'Maths', 'present');
            INSERT INTO "dbo"."Achievements" ("TenantId", "StudentId", "Title", "AwardedOn")
                VALUES (@t, @s, 'Chess', DATE '2026-09-01');
            """,
            new { t = tenantId, u = Guid.NewGuid(), r = Guid.NewGuid(), c = Guid.NewGuid(), s = Guid.NewGuid() });
    }

    [Fact]
    public async Task Deleting_an_unknown_client_returns_404()
    {
        await using var app = App();
        var res = await DeleteAsync(PlatformClient(app), Guid.NewGuid());
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_client_with_students_is_refused_with_counts()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var id = await CreateActiveClientAsync(client);

        var ctx = new TenantContext();
        ctx.Set(id, Guid.NewGuid(), false);
        await using (var conn = await new NpgsqlConnectionFactory(fx.ConnectionString, ctx).OpenAsync())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO "dbo"."Students" ("Id", "TenantId", "AdmissionNo", "Name")
                VALUES (@a, @id, 'A-1', 'One'), (@b, @id, 'A-2', 'Two')
                """,
                new { a = Guid.NewGuid(), b = Guid.NewGuid(), id });
        }

        var res = await DeleteAsync(client, id);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("2 student(s)");

        (await CountAsync("""SELECT count(*) FROM "dbo"."Tenants" WHERE "Id" = @tenantId""", id)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_an_empty_client_removes_it_and_its_dependent_rows()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var id = await CreateActiveClientAsync(client);

        (await CountAsync("""SELECT count(*) FROM "dbo"."Subscriptions" WHERE "TenantId" = @tenantId""", id))
            .Should().BeGreaterThan(0, "activation should have created billing rows for the delete to cascade through");

        var otherSchool = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, otherSchool);
        await SeedRestoredDependentsAsync(id);
        await SeedRestoredDependentsAsync(otherSchool);

        (await DeleteAsync(client, id)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CountAsync("""SELECT count(*) FROM "dbo"."Tenants" WHERE "Id" = @tenantId""", id)).Should().Be(0);
        (await CountAsync("""SELECT count(*) FROM "dbo"."Subscriptions" WHERE "TenantId" = @tenantId""", id)).Should().Be(0);
        (await client.GetAsync($"/v1/clients/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DeleteAsync(client, id)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        foreach (var table in RestoredDependents)
        {
            (await CountAsync($"""SELECT count(*) FROM "dbo"."{table}" WHERE "TenantId" = @tenantId""", id))
                .Should().Be(0, $"{table} rows of the deleted school must not be orphaned");
            (await CountAsync($"""SELECT count(*) FROM "dbo"."{table}" WHERE "TenantId" = @tenantId""", otherSchool))
                .Should().Be(1, $"{table} rows of other schools must survive");
        }
    }
}
