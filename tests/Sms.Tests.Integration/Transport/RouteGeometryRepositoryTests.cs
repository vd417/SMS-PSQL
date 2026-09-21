using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class RouteGeometryRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private async Task<(Guid tenantId, Guid routeId)> SeedTenantAndRoute()
    {
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@RouteId, @TenantId, 'Test Route')",
            new { RouteId = routeId, TenantId = tenantId });
        return (tenantId, routeId);
    }

    [Fact]
    public async Task GetAsync_returns_null_when_no_row_exists()
    {
        var tenantId = Guid.NewGuid();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<RouteGeometryRepository>();

        var result = await repo.GetAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_then_GetAsync_round_trips_the_row()
    {
        var (tenantId, routeId) = await SeedTenantAndRoute();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<RouteGeometryRepository>();

        await repo.UpsertAsync(tenantId, routeId, "hash-1", "google-encoded-polyline",
            "abc123", 4210, 780, "google-routes", DateTime.UtcNow);

        var row = await repo.GetAsync(routeId);
        Assert.NotNull(row);
        Assert.Equal("hash-1", row!.StopSequenceHash);
        Assert.Equal("abc123", row.EncodedPolyline);
    }

    [Fact]
    public async Task UpsertAsync_replaces_existing_row_for_same_route()
    {
        var (tenantId, routeId) = await SeedTenantAndRoute();
        await using var app = App();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<RouteGeometryRepository>();
        await repo.UpsertAsync(tenantId, routeId, "hash-1", "google-encoded-polyline", "abc", 100, 10, "google-routes", DateTime.UtcNow);

        await repo.UpsertAsync(tenantId, routeId, "hash-2", "google-encoded-polyline", "xyz", 200, 20, "google-routes", DateTime.UtcNow);

        var row = await repo.GetAsync(routeId);
        Assert.Equal("hash-2", row!.StopSequenceHash);
        Assert.Equal("xyz", row.EncodedPolyline);
    }
}
