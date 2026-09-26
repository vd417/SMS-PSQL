using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Tests.Integration.Saas;

[Collection("sql")]
public class OtpLoginTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    // The real code is random and only printed to the console, so after requesting an OTP we overwrite
    // its stored hash with the hash of a known code ("123456"). This keeps the test deterministic
    // without scraping stdout; production never exposes the code.
    [Fact]
    public async Task Otp_request_then_verify_issues_tokens_for_known_email()
    {
        var ctx = new TenantContext(); ctx.Set(null, Guid.NewGuid(), true);
        var factory = new NpgsqlConnectionFactory(fx.ConnectionString, ctx);
        var email = $"otp{Guid.NewGuid():N}@x.com";
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                """INSERT INTO "dbo"."Users" ("Id", "Email", "IsPlatform") VALUES (gen_random_uuid(),@e,false)""",
                new { e = email });

        await using var app = App();
        var client = app.CreateClient();

        // Registered email → 200 (OTP generated).
        (await client.PostAsJsonAsync("/v1/auth/otp/request", new { identifier = email }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Unregistered email → 404 with "Email is not registered." and NO OTP generated.
        var unknownEmail = await client.PostAsJsonAsync("/v1/auth/otp/request", new { identifier = "nobody@x.com" });
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var err = JsonDocument.Parse(await unknownEmail.Content.ReadAsStringAsync()))
            err.RootElement.GetProperty("error").GetProperty("message").GetString()
                .Should().Be("Email is not registered.");

        // Unregistered phone → 404 with the phone-tailored message.
        var unknownPhone = await client.PostAsJsonAsync("/v1/auth/otp/request", new { identifier = "+910000000000" });
        unknownPhone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var err = JsonDocument.Parse(await unknownPhone.Content.ReadAsStringAsync()))
            err.RootElement.GetProperty("error").GetProperty("message").GetString()
                .Should().Be("Phone is not registered.");

        // Read the issued code from the DB (sha256 hash stored), brute the 6-digit space is avoided by
        // recomputing the hash for each candidate is impractical; instead overwrite with a known code.
        await using (var c = await factory.OpenAsync())
            await c.ExecuteAsync(
                "UPDATE \"dbo\".\"OtpCodes\" SET \"CodeHash\" = @h WHERE \"Identifier\" = @id",
                new { id = email, h = Sha256Hex("123456") });

        var verify = await client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { identifier = email, code = "123456" });
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetProperty("access_token").GetString()
            .Should().NotBeNullOrEmpty();

        // A wrong code → 401.
        (await client.PostAsJsonAsync("/v1/auth/otp/verify", new { identifier = email, code = "000000" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string Sha256Hex(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
