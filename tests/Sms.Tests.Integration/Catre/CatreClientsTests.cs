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
public class CatreClientsTests(PostgresFixture fx)
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

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), isPlatform: true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> CreatePlanAsync(HttpClient client, string name, string tier, decimal price)
    {
        var res = await client.PostAsJsonAsync("/v1/plans", new
        {
            name, tier, pricing = "flat", price, period = "month",
            features = new[] { "sis.students", "academics" },
            limits = new { students = 1200, staff = 120, storage_gb = 50 },
            visibility = "published", audience = "all"
        });
        var data = await Data(res, HttpStatusCode.Created);
        return data.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Plan_upsert_then_list_and_get()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var planId = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var get = await Data(await client.GetAsync($"/v1/plans/{planId}"), HttpStatusCode.OK);
        get.GetProperty("name").GetString().Should().Be("Gold");
        get.GetProperty("features").EnumerateArray().Select(e => e.GetString()).Should().Contain("academics");
        get.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(1200);

        var list = await Data(await client.GetAsync("/v1/plans"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(planId);
    }

    [Fact]
    public async Task Client_lifecycle_create_get_list_status_changeplan()
    {
        await using var app = App();
        var client = PlatformClient(app);

        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);
        var platinum = await CreatePlanAsync(client, "Platinum", "platinum", 29999);

        // create
        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Mumbai, MH",
            admin_name = "Priya Sharma", admin_email = "admin@greenwood.edu.in", plan_id = gold, trial_days = 14
        }), HttpStatusCode.Created);

        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("status").GetString().Should().Be("trial");
        created.GetProperty("plan_name").GetString().Should().Be("Gold");
        created.GetProperty("tier").GetString().Should().Be("gold");
        created.GetProperty("mrr").GetDecimal().Should().Be(14999);
        created.GetProperty("limits").GetProperty("students").GetInt32().Should().Be(1200);

        // get
        var got = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        got.GetProperty("name").GetString().Should().Be("Greenwood High");

        // list
        var list = await Data(await client.GetAsync("/v1/clients"), HttpStatusCode.OK);
        list.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Should().Contain(id);

        // activate
        var activated = await Data(
            await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }), HttpStatusCode.OK);
        activated.GetProperty("status").GetString().Should().Be("active");

        // change plan
        var changed = await Data(
            await client.PostAsJsonAsync($"/v1/clients/{id}/change-plan", new { plan_id = platinum }), HttpStatusCode.OK);
        changed.GetProperty("tier").GetString().Should().Be("platinum");
        changed.GetProperty("plan_name").GetString().Should().Be("Platinum");
        changed.GetProperty("mrr").GetDecimal().Should().Be(29999);
    }

    [Fact]
    public async Task Client_create_persists_and_returns_contact_and_address()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Mumbai, MH",
            admin_name = "Priya Sharma", admin_email = "admin@greenwood.edu.in", admin_phone = "+91 98200 11111",
            address = "12 MG Road, Fort, Mumbai 400001", plan_id = gold, trial_days = 14
        }), HttpStatusCode.Created);

        created.GetProperty("contact_name").GetString().Should().Be("Priya Sharma");
        created.GetProperty("contact_email").GetString().Should().Be("admin@greenwood.edu.in");
        created.GetProperty("contact_phone").GetString().Should().Be("+91 98200 11111");
        created.GetProperty("address").GetString().Should().Be("12 MG Road, Fort, Mumbai 400001");

        var id = created.GetProperty("id").GetGuid();
        var got = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        got.GetProperty("address").GetString().Should().Be("12 MG Road, Fort, Mumbai 400001");

        var list = await Data(await client.GetAsync("/v1/clients"), HttpStatusCode.OK);
        var row = list.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        row.GetProperty("contact_email").GetString().Should().Be("admin@greenwood.edu.in");
    }

    [Fact]
    public async Task Client_create_persists_id_proof()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Maharashtra",
            admin_name = "Priya Sharma", admin_email = "admin@greenwood.edu.in", admin_phone = "+91 98200 11111",
            plan_id = gold, trial_days = 14,
            id_proof_type = "pan", id_proof_url = "data:application/pdf;base64,JVBERi0="
        }), HttpStatusCode.Created);

        created.GetProperty("id_proof_type").GetString().Should().Be("pan");
        created.GetProperty("id_proof_url").GetString().Should().StartWith("data:application/pdf");

        var id = created.GetProperty("id").GetGuid();
        var got = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        got.GetProperty("id_proof_type").GetString().Should().Be("pan");
        got.GetProperty("id_proof_url").GetString().Should().StartWith("data:application/pdf");
    }

    [Fact]
    public async Task Client_create_rejects_bad_id_proof_type()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var res = await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Bad Type", slug = $"bad-type-{Guid.NewGuid():N}", country = "Maharashtra",
            admin_name = "A", admin_email = "a@b.com", admin_phone = "+91 90000 00000",
            plan_id = gold, trial_days = 14,
            id_proof_type = "passport", id_proof_url = "data:application/pdf;base64,JVBERi0="
        });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Client_create_rejects_oversize_id_proof()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var res = await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Too Big", slug = $"too-big-{Guid.NewGuid():N}", country = "Maharashtra",
            admin_name = "A", admin_email = "a@b.com", admin_phone = "+91 90000 00000",
            plan_id = gold, trial_days = 14,
            id_proof_type = "pan", id_proof_url = "data:application/pdf;base64," + new string('A', 3_500_001)
        });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task School_count_shows_live_enrolled_while_billed_count_holds_committed_floor()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        // Onboarding commits to 350 students for billing, but nobody is enrolled in the SIS yet.
        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Mumbai, MH",
            admin_name = "Priya Sharma", admin_email = $"admin-{Guid.NewGuid():N}@greenwood.edu.in",
            plan_id = gold, trial_days = 14, students_count = 350
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        // School profile: the student count is who is actually enrolled right now (nobody),
        // while the billed count carries the committed floor the first invoice is charged on.
        var got = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        got.GetProperty("enrolled_students").GetInt32().Should().Be(0);
        got.GetProperty("students_count").GetInt32().Should().Be(350);

        // Same split in the portfolio list.
        var list = await Data(await client.GetAsync("/v1/clients"), HttpStatusCode.OK);
        var row = list.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        row.GetProperty("enrolled_students").GetInt32().Should().Be(0);
        row.GetProperty("students_count").GetInt32().Should().Be(350);
    }

    [Fact]
    public async Task Enrolled_count_reflects_active_students_and_billed_count_tracks_the_higher_of_the_two()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        // Commit to 100 at onboarding, then enrol 3 active + 1 inactive student in the SIS.
        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Riverside", slug = $"riverside-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "A", admin_email = $"admin-{Guid.NewGuid():N}@riverside.edu.in",
            plan_id = gold, trial_days = 14, students_count = 100
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        var factory = PlatformFactory();
        await using (var c = await factory.OpenAsync())
        {
            await c.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Status\") VALUES " +
                "(gen_random_uuid(), @t, 'A1', 'Student 1', 'active'), " +
                "(gen_random_uuid(), @t, 'A2', 'Student 2', 'active'), " +
                "(gen_random_uuid(), @t, 'A3', 'Student 3', 'active'), " +
                "(gen_random_uuid(), @t, 'A4', 'Student 4', 'inactive')",
                new { t = id });
        }

        var got = await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK);
        // enrolled = active students only (3), not the committed 100 and not the inactive row.
        got.GetProperty("enrolled_students").GetInt32().Should().Be(3);
        // billed = max(live enrolled, committed floor) = max(3, 100) = 100.
        got.GetProperty("students_count").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Status_change_response_carries_live_enrolled_count()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Hilltop", slug = $"hilltop-{Guid.NewGuid():N}", country = "Nashik, MH",
            admin_name = "A", admin_email = $"admin-{Guid.NewGuid():N}@hilltop.edu.in",
            plan_id = gold, trial_days = 14, students_count = 100
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        var factory = PlatformFactory();
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"AdmissionNo\", \"Name\", \"Status\") VALUES " +
                "(gen_random_uuid(), @t, 'A1', 'S 1', 'active'), (gen_random_uuid(), @t, 'A2', 'S 2', 'active')",
                new { t = id });

        // A mutation response (status change) must reflect live enrolment, not a stale 0.
        var activated = await Data(
            await client.PostAsJsonAsync($"/v1/clients/{id}/status", new { status = "active" }), HttpStatusCode.OK);
        activated.GetProperty("enrolled_students").GetInt32().Should().Be(2);
        activated.GetProperty("students_count").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Clients_requires_platform_auth()
    {
        await using var app = App();
        var res = await app.CreateClient().GetAsync("/v1/clients"); // no token
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Client_create_seeds_onboarding_card_with_contact()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Mumbai, MH",
            admin_name = "Priya Sharma", admin_email = "admin@greenwood.edu.in", admin_phone = "+91 98200 11111",
            address = "12 MG Road, Fort, Mumbai 400001", plan_id = gold, trial_days = 14
        }), HttpStatusCode.Created);
        // AllocateUniqueSlugAsync may truncate long desired slugs — match the persisted value.
        var slug = created.GetProperty("slug").GetString();

        var board = await Data(await client.GetAsync("/v1/onboarding"), HttpStatusCode.OK);
        var card = board.EnumerateArray().Single(e => e.GetProperty("slug").GetString() == slug);
        card.GetProperty("contact_name").GetString().Should().Be("Priya Sharma");
        card.GetProperty("contact_email").GetString().Should().Be("admin@greenwood.edu.in");
        card.GetProperty("contact_phone").GetString().Should().Be("+91 98200 11111");
        card.GetProperty("address").GetString().Should().Be("12 MG Road, Fort, Mumbai 400001");
    }

    [Fact]
    public async Task Onboarding_saves_the_founding_account_as_school_owner()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var gold = await CreatePlanAsync(client, "Gold", "gold", 14999);

        var email = $"owner-{Guid.NewGuid():N}@greenwood.edu.in";
        await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Greenwood High", slug = $"greenwood-{Guid.NewGuid():N}", country = "Mumbai, MH",
            admin_name = "Priya Sharma", admin_email = email, plan_id = gold, trial_days = 14
        }), System.Net.HttpStatusCode.Created);

        var ctx = new Sms.Shared.Kernel.Tenancy.TenantContext();
        ctx.Set(null, Guid.NewGuid(), true); // platform context bypasses RLS on dbo.Users
        var factory = new Sms.Shared.Kernel.Data.NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        await using var c = await factory.OpenAsync();
        var roles = (await Dapper.SqlMapper.QueryAsync<string>(c,
            "SELECT ur.\"Role\" FROM \"dbo\".\"UserRoles\" ur JOIN \"dbo\".\"Users\" u ON u.\"Id\" = ur.\"UserId\" WHERE u.\"Email\" = @e",
            new { e = email })).ToList();

        roles.Should().ContainSingle().Which.Should().Be("school.owner");
    }
}
