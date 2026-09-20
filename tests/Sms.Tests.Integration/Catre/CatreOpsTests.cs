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

[Collection("sql")]
public class CatreOpsTests(PostgresFixture fx)
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

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Onboarding_create_advance_and_toggle_checklist()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var created = await Data(await client.PostAsJsonAsync("/v1/onboarding", new
        {
            name = "Lead School", slug = $"lead-{Guid.NewGuid():N}", owner = "Karthik", value = 4999
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("stage").GetString().Should().Be("lead");
        created.GetProperty("checklist").GetArrayLength().Should().Be(5);
        // "Account created" is seeded done by Onboarding_Create (account already exists)
        created.GetProperty("done").GetInt32().Should().Be(1);

        var advanced = await Data(
            await client.PostAsJsonAsync($"/v1/onboarding/{id}/advance", new { stage = "trial" }), HttpStatusCode.OK);
        advanced.GetProperty("stage").GetString().Should().Be("trial");

        var patched = await Data(await client.PatchAsJsonAsync($"/v1/onboarding/{id}/checklist",
            new { label = "Admin invited", done = true }), HttpStatusCode.OK);
        // "Account created" (seeded done) + "Admin invited" (just ticked) = 2 done items
        patched.GetProperty("done").GetInt32().Should().Be(2);

        var list = await Data(await client.GetAsync("/v1/onboarding"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);
    }

    [Fact]
    public async Task Ticket_create_add_message_and_update()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var created = await Data(await client.PostAsJsonAsync("/v1/tickets", new
        {
            subject = "Cannot import students", priority = "high"
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("messages").GetArrayLength().Should().Be(0);

        var withMsg = await Data(await client.PostAsJsonAsync($"/v1/tickets/{id}/messages",
            new { text = "We are looking into it." }), HttpStatusCode.Created);
        withMsg.GetProperty("messages_count").GetInt32().Should().Be(1);
        withMsg.GetProperty("messages").GetArrayLength().Should().Be(1);
        withMsg.GetProperty("messages")[0].GetProperty("role").GetString().Should().Be("agent");

        var updated = await Data(await client.PatchAsJsonAsync($"/v1/tickets/{id}",
            new { status = "resolved", assignee = "Rohan" }), HttpStatusCode.OK);
        updated.GetProperty("status").GetString().Should().Be("resolved");
        updated.GetProperty("assignee").GetString().Should().Be("Rohan");
    }

    [Fact]
    public async Task Team_invite_list_and_update()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var employeeId = $"EMP-{Guid.NewGuid():N}"[..16];
        var invited = await Data(await client.PostAsJsonAsync("/v1/team", new
        {
            name = "Asha",
            email = $"asha{Guid.NewGuid():N}@catre.io",
            role = "support",
            employee_id = employeeId,
        }), HttpStatusCode.Created);
        var id = invited.GetProperty("id").GetGuid();
        invited.GetProperty("status").GetString().Should().Be("active");
        invited.GetProperty("employee_id").GetString().Should().Be(employeeId);

        var list = await Data(await client.GetAsync("/v1/team"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        var updated = await Data(await client.PatchAsJsonAsync($"/v1/team/{id}",
            new { status = "invited" }), HttpStatusCode.OK);
        updated.GetProperty("status").GetString().Should().Be("invited");
    }

    [Fact]
    public async Task Audit_list_returns_seeded_entry()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var action = "suspend client " + Guid.NewGuid().ToString("N");
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT dbo.AuditLog (Id, ActorId, ActorName, Role, Action, Target, Kind) " +
                "VALUES (NEWID(), @a, 'Rohan', 'admin', @act, 'Greenwood', 'suspend')",
                new { a = Guid.NewGuid(), act = action });

        await using var app = App();
        var client = PlatformClient(app);
        var list = await Data(await client.GetAsync("/v1/audit"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("action").GetString()).Should().Contain(action);
    }

    [Fact]
    public async Task Reports_revenue_and_csv()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var planId = (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Gold", tier = "gold", pricing = "flat", price = 14999, period = "month",
            limits = new { students = 1000, staff = 100, storage_gb = 40 }, visibility = "published", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var clientId = (await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood", slug = $"g-{Guid.NewGuid():N}", plan_id = planId, trial_days = 14
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await client.PostAsJsonAsync($"/v1/clients/{clientId}/status", new { status = "active" });

        var rev = await Data(await client.GetAsync("/v1/reports/revenue"), HttpStatusCode.OK);
        rev.GetProperty("arr").GetDecimal().Should().BeGreaterThanOrEqualTo(14999 * 12);
        rev.GetProperty("plan_performance").GetArrayLength().Should().BeGreaterThan(0);

        var csv = await client.GetAsync("/v1/reports/clients.csv");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (await csv.Content.ReadAsStringAsync()).Should().StartWith("client,status,plan,mrr");
    }

    // The Catre plan editor posts no `tier` (it carries `feature_tiers` instead, which the
    // backend ignores). Tier must default from the name so the save succeeds — not 500.
    [Fact]
    public async Task Create_plan_without_tier_defaults_tier_from_name()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var created = await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Sliver", pricing = "per_student", price = 0, per_student = 25, min_students = 100,
            period = "month", audience = "all", band = "200", visibility = "draft", offer = (object?)null,
            feature_tiers = new { }, features = new[] { "sis.students", "academics" },
            limits = new { students = 158, staff = 39, storage_gb = 47 }
        }), HttpStatusCode.Created);

        created.GetProperty("tier").GetString().Should().Be("sliver");
        created.GetProperty("name").GetString().Should().Be("Sliver");
        created.GetProperty("per_student").GetDecimal().Should().Be(25);
    }

    [Fact]
    public async Task Patch_plan_updates_existing_and_404s_unknown()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var id = (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Bronze", tier = "bronze", pricing = "flat", price = 999, period = "month",
            limits = new { students = 100, staff = 10, storage_gb = 5 }, visibility = "draft", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var patched = await Data(await client.PatchAsJsonAsync($"/v1/plans/{id}", new
        {
            name = "Bronze", tier = "bronze", pricing = "flat", price = 1299, period = "month",
            limits = new { students = 100, staff = 10, storage_gb = 5 }, visibility = "draft", audience = "all"
        }), HttpStatusCode.OK);
        patched.GetProperty("price").GetDecimal().Should().Be(1299);

        var missing = await client.PatchAsJsonAsync($"/v1/plans/{Guid.NewGuid()}", new
        {
            name = "Ghost", tier = "ghost", pricing = "flat", price = 1, period = "month",
            limits = new { students = 1, staff = 1, storage_gb = 1 }, visibility = "draft", audience = "all"
        });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publish_and_unpublish_plan_and_404s_unknown()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var id = (await Data(await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Copper", tier = "copper", pricing = "flat", price = 499, period = "month",
            limits = new { students = 50, staff = 5, storage_gb = 2 }, visibility = "draft", audience = "all"
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Frontend's published vocab is "public" (not "published") — stored as-is so ?visibility= filtering matches.
        var published = await Data(
            await client.PostAsJsonAsync($"/v1/plans/{id}/publish", new { visibility = "public" }),
            HttpStatusCode.OK);
        published.GetProperty("visibility").GetString().Should().Be("public");

        var unpublished = await Data(
            await client.PostAsJsonAsync($"/v1/plans/{id}/publish", new { visibility = "draft" }),
            HttpStatusCode.OK);
        unpublished.GetProperty("visibility").GetString().Should().Be("draft");

        var empty = await client.PostAsJsonAsync($"/v1/plans/{id}/publish", new { visibility = "" });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missing = await client.PostAsJsonAsync(
            $"/v1/plans/{Guid.NewGuid()}/publish", new { visibility = "public" });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
