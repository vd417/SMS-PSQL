using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Catre;

[Collection("sql")]
public class ClientBoardTests(PostgresFixture fx)
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

    private static async Task<Guid> CreatePlanAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/v1/plans", new
        {
            name = "Gold", tier = "gold", pricing = "flat", price = 9999m, period = "month",
            features = new[] { "sis.students" },
            limits = new { students = 1000, staff = 100, storage_gb = 50 },
            visibility = "published", audience = "all",
        });
        return (await Data(res, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_persists_and_returns_board()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var plan = await CreatePlanAsync(client);

        var created = await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "Board School", slug = $"board-{Guid.NewGuid():N}", country = "Pune, MH",
            admin_name = "Admin", admin_email = "admin@board.edu.in", plan_id = plan, trial_days = 14,
            board = "CBSE",
        }), HttpStatusCode.Created);

        created.GetProperty("board").GetString().Should().Be("CBSE");

        var id = created.GetProperty("id").GetGuid();
        (await Data(await client.GetAsync($"/v1/clients/{id}"), HttpStatusCode.OK))
            .GetProperty("board").GetString().Should().Be("CBSE");
    }

    [Fact]
    public async Task Patch_updates_board_including_a_custom_name()
    {
        await using var app = App();
        var client = PlatformClient(app);
        var plan = await CreatePlanAsync(client);

        var id = (await Data(await client.PostAsJsonAsync("/v1/clients", new
        {
            name = "No Board School", slug = $"noboard-{Guid.NewGuid():N}",
            admin_name = "Admin", admin_email = "admin2@board.edu.in", plan_id = plan, trial_days = 14,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Preset board.
        (await Data(await client.PatchAsJsonAsync($"/v1/clients/{id}", new { board = "ICSE" }), HttpStatusCode.OK))
            .GetProperty("board").GetString().Should().Be("ICSE");

        // Custom board name (the "Other…" path).
        (await Data(await client.PatchAsJsonAsync($"/v1/clients/{id}", new { board = "Maharashtra State Board" }), HttpStatusCode.OK))
            .GetProperty("board").GetString().Should().Be("Maharashtra State Board");

        // A non-board patch leaves board unchanged.
        (await Data(await client.PatchAsJsonAsync($"/v1/clients/{id}", new { contact_name = "New Admin" }), HttpStatusCode.OK))
            .GetProperty("board").GetString().Should().Be("Maharashtra State Board");
    }
}
