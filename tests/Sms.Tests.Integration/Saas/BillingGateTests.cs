using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Time;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class BillingGateTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private async Task<Guid> SeedTenant(string status)
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var id = Guid.NewGuid();
        await using var c = await factory.OpenAsync();
        await c.ExecuteAsync(
            """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@id,@n,@s,@st,'gold')""",
            new { id, n = "T", s = $"t{id:N}", st = status });
        return id;
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Past_due_allows_reads_blocks_writes()
    {
        var tid = await SeedTenant("past_due");
        await using var app = App();
        var client = ClientFor(app, tid);

        (await client.GetAsync("/v1/students")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/v1/students", new { name = "x", admission_no = "A1" }))
            .StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task Suspended_blocks_everything_except_auth()
    {
        var tid = await SeedTenant("suspended");
        await using var app = App();
        var client = ClientFor(app, tid);

        (await client.GetAsync("/v1/students")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // auth still reachable (422 = validation, NOT blocked by the gate)
        (await client.PostAsJsonAsync("/v1/auth/login", new { }))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
