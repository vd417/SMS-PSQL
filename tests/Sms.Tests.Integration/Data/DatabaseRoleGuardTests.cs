using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Sms.Tests.Integration.Data;

/// DatabaseRoleGuard: outside Development the API must refuse to start when its connection role
/// bypasses RLS (superuser or BYPASSRLS), and start normally as sms_app.
[Collection("sql")]
public class DatabaseRoleGuardTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private static WebApplicationFactory<Program> App(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", connectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public void Api_refuses_to_start_outside_development_when_connected_as_a_superuser()
    {
        var db = new NpgsqlConnectionStringBuilder(fx.ConnectionString).Database!;
        using var app = App(TestPostgresServer.ForDatabase(db));

        var start = () => app.CreateClient();

        start.Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("bypasses row-level security"));
    }

    [Fact]
    public async Task Api_starts_when_connected_as_sms_app()
    {
        await using var app = App(fx.ConnectionString);
        var res = await app.CreateClient().GetAsync("/health/ready");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
