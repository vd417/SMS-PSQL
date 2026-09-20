using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class FeeHeadsTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static HttpClient AuthedClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static WebApplicationFactory<Program> App(PostgresFixture fx) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task ListHeads_returns_200_and_materializes_including_the_transport_flag()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Library" });

        var res = await client.GetAsync("/v1/fees/heads");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThan(0);
        // The Dapper materialization bug this test guards against would throw before any
        // response body is produced — reaching here already proves the fix, but assert the
        // field is actually present and typed correctly too.
        data[0].TryGetProperty("is_transport_fee_head", out var flag).Should().BeTrue();
        flag.ValueKind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    public async Task Creating_a_head_with_is_transport_fee_head_true_round_trips()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var create = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Transport", is_transport_fee_head = true });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        createDoc.RootElement.GetProperty("data").GetProperty("is_transport_fee_head").GetBoolean().Should().BeTrue();

        var list = await client.GetAsync("/v1/fees/heads");
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var row = listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == "Transport");
        row.GetProperty("is_transport_fee_head").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_head_without_specifying_the_flag_defaults_to_false()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var create = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Exam" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("is_transport_fee_head").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Updating_a_head_can_flip_the_transport_flag_true_then_false()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var create = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Bus Fee" });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var toTrue = await client.PatchAsJsonAsync($"/v1/fees/heads/{id}", new { is_transport_fee_head = true });
        toTrue.StatusCode.Should().Be(HttpStatusCode.OK);
        using var trueDoc = JsonDocument.Parse(await toTrue.Content.ReadAsStringAsync());
        trueDoc.RootElement.GetProperty("data").GetProperty("is_transport_fee_head").GetBoolean().Should().BeTrue();

        var toFalse = await client.PatchAsJsonAsync($"/v1/fees/heads/{id}", new { is_transport_fee_head = false });
        using var falseDoc = JsonDocument.Parse(await toFalse.Content.ReadAsStringAsync());
        falseDoc.RootElement.GetProperty("data").GetProperty("is_transport_fee_head").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Updating_a_head_without_specifying_the_flag_leaves_it_unchanged()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var create = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Sports", is_transport_fee_head = true });
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var renamed = await client.PatchAsJsonAsync($"/v1/fees/heads/{id}", new { name = "Sports & Games" });
        using var doc = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("name").GetString().Should().Be("Sports & Games");
        doc.RootElement.GetProperty("data").GetProperty("is_transport_fee_head").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_fee_head_name_still_returns_409()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var first = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Academic" });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var dupe = await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Academic" });
        dupe.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await dupe.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("already a fee type");
    }

    [Fact]
    public async Task Create_head_with_a_description_then_list_and_update_it()
    {
        await using var app = App(fx);
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = AuthedClient(app, tenantId, Guid.NewGuid(), "school.principal");

        var created = await client.PostAsJsonAsync("/v1/fees/heads",
            new { name = "Trip", description = "Annual educational trip to Mumbai, Nov 2026" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var headId = createdDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var list = await client.GetAsync("/v1/fees/heads");
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var row = listDoc.RootElement.GetProperty("data").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == headId);
        row.GetProperty("description").GetString().Should().Be("Annual educational trip to Mumbai, Nov 2026");

        var updated = await client.PatchAsJsonAsync($"/v1/fees/heads/{headId}", new { description = "Trip moved to Dec 2026" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var updatedDoc = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        updatedDoc.RootElement.GetProperty("data").GetProperty("description").GetString().Should().Be("Trip moved to Dec 2026");
    }
}
