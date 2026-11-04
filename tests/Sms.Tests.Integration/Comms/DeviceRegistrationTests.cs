using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class DeviceRegistrationTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, ["driver"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private async Task<int> RowCount(Guid tenantId, Guid userId, string pushToken)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM \"dbo\".\"ParentDevices\" WHERE \"UserId\" = @u AND \"ExpoPushToken\" = @k",
            new { u = userId, k = pushToken });
    }

    [Fact]
    public async Task Registering_a_device_returns_200_and_stores_one_row()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]";

        var res = await Client(app, tenantId, userId).PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = pushToken, platform = "ios" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowCount(tenantId, userId, pushToken)).Should().Be(1);
    }

    [Fact]
    public async Task Re_registering_the_same_token_is_idempotent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]";
        var client = Client(app, tenantId, userId);

        await client.PostAsJsonAsync("/v1/me/devices", new { expo_push_token = pushToken, platform = "ios" });
        var second = await client.PostAsJsonAsync("/v1/me/devices", new { expo_push_token = pushToken, platform = "android" });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RowCount(tenantId, userId, pushToken)).Should().Be(1, "upsert must not create a duplicate");
    }

    [Fact]
    public async Task Rejects_an_unsupported_platform_and_a_blank_token()
    {
        await using var app = App();
        var client = Client(app, Guid.NewGuid(), Guid.NewGuid());

        var badPlatform = await client.PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "ExponentPushToken[x]", platform = "web" });
        badPlatform.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var blankToken = await client.PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "", platform = "ios" });
        blankToken.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        await using var app = App();
        var res = await app.CreateClient().PostAsJsonAsync("/v1/me/devices",
            new { expo_push_token = "ExponentPushToken[x]", platform = "ios" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
