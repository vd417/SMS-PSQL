using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class OwnerFeeSummaryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private NpgsqlConnectionFactory PlatformFactory()
    {
        var ctx = new TenantContext();
        ctx.Set(null, Guid.NewGuid(), true);
        return new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
    }

    [Fact]
    public async Task Fee_summary_returns_school_wise_collected_for_owner_portfolio()
    {
        var hasher = new PasswordHasher();
        var factory = PlatformFactory();
        var email = $"owner{Guid.NewGuid():N}@fees.test";
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        await using (var c = await factory.OpenAsync())
        {
            await c.ExecuteAsync(
                """INSERT INTO "dbo"."Tenants" ("Id", "Name", "Slug", "Status", "Tier") VALUES (@a,'Alpha High',@sa,'active','gold'),(@b,'Beta High',@sb,'active','silver')""",
                new { a = t1, sa = "a-" + t1.ToString("N")[..8], b = t2, sb = "b-" + t2.ToString("N")[..8] });

            var u1 = await c.QuerySingleAsync<Guid>(
                """INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "PasswordHash", "IsPlatform") VALUES (gen_random_uuid(),@t,@e,@h,false) RETURNING "Id" """,
                new { t = t1, e = email, h = hasher.Hash("Pass123!") });
            var u2 = await c.QuerySingleAsync<Guid>(
                """INSERT INTO "dbo"."Users" ("Id", "TenantId", "Email", "PasswordHash", "IsPlatform") VALUES (gen_random_uuid(),@t,@e,@h,false) RETURNING "Id" """,
                new { t = t2, e = email, h = hasher.Hash("Pass123!") });
            await c.ExecuteAsync(
                """INSERT INTO "dbo"."UserRoles" ("UserId", "Role") VALUES (@u1,'school.owner'),(@u2,'school.owner')""",
                new { u1, u2 });

            await c.ExecuteAsync(
                """
                INSERT INTO "dbo"."FeePayments" ("Id", "TenantId", "StudentId", "StudentName", "ClassLabel", "FeeType", "Amount", "Method", "Ref", "Date")
                  VALUES (gen_random_uuid(),@t1,@s1,'A','X-A','academic',10000,'UPI','R1',now()::date),
                         (gen_random_uuid(),@t2,@s2,'B','IX-B','academic',2500,'Cash','R2',now()::date)
                """,
                new { t1, t2, s1, s2 });

            await c.ExecuteAsync(
                """
                INSERT INTO "dbo"."FeeInvoices" ("Id", "TenantId", "StudentId", "Period", "DueDate", "Amount", "Status")
                  VALUES (gen_random_uuid(),@t1,@s1,'2026-07',now()::date,3000,'due')
                """,
                new { t1, s1 });
        }

        await using var app = App();
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync("/v1/auth/login", new { email, password = "Pass123!" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ldoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var access = ldoc.RootElement.GetProperty("data").GetProperty("access_token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", access);

        var res = await client.GetAsync("/v1/me/schools/fee-summary");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("totals").GetProperty("collected").GetDecimal().Should().Be(12500);
        data.GetProperty("totals").GetProperty("outstanding").GetDecimal().Should().Be(3000);

        var schools = data.GetProperty("schools").EnumerateArray().ToDictionary(
            e => e.GetProperty("tenant_id").GetGuid(), e => e);
        schools[t1].GetProperty("collected").GetDecimal().Should().Be(10000);
        schools[t1].GetProperty("outstanding").GetDecimal().Should().Be(3000);
        schools[t2].GetProperty("collected").GetDecimal().Should().Be(2500);
        schools[t2].GetProperty("name").GetString().Should().Be("Beta High");
    }
}
