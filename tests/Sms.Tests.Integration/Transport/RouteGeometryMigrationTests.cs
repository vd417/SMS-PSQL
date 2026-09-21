using Dapper;
using Npgsql;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Tests.Integration;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class RouteGeometryMigrationTests(PostgresFixture fx)
{
    [Fact]
    public async Task RouteGeometries_table_exists_with_expected_columns()
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        var columns = (await conn.QueryAsync<string>(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'RouteGeometries'")).ToHashSet();

        Assert.Contains("RouteId", columns);
        Assert.Contains("TenantId", columns);
        Assert.Contains("StopSequenceHash", columns);
        Assert.Contains("Format", columns);
        Assert.Contains("EncodedPolyline", columns);
        Assert.Contains("DistanceMeters", columns);
        Assert.Contains("DurationSeconds", columns);
        Assert.Contains("Provider", columns);
        Assert.Contains("GeneratedAt", columns);
    }

    [Fact]
    public async Task RouteGeometriesTenantPolicy_security_policy_exists_and_targets_the_table()
    {
        // Column-existence alone (above) says nothing about whether the RLS conversion actually
        // applied, or whether it was later dropped. SQL Server's single CREATE SECURITY POLICY
        // (one FILTER + one BLOCK predicate) became four native Postgres per-command policies
        // (SELECT/UPDATE/DELETE/INSERT, see 07_rls_policies.sql) — query pg_class/pg_policies
        // directly, the Postgres-native way to audit RLS coverage, preserving the same intent:
        // row security is enabled, and both a filter (USING) and a block (WITH CHECK) predicate
        // exist and target this table.
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var rlsEnabled = await conn.QuerySingleAsync<bool>(
            "SELECT relrowsecurity FROM pg_class WHERE relname = 'RouteGeometries'");
        rlsEnabled.Should().BeTrue("RLS must be enabled on RouteGeometries");

        var policies = (await conn.QueryAsync<(string PolicyName, string? Qual, string? WithCheck)>(
            @"SELECT policyname AS PolicyName, qual AS Qual, with_check AS WithCheck
              FROM pg_policies
              WHERE schemaname = 'dbo' AND tablename = 'RouteGeometries'")).ToList();

        policies.Should().NotBeEmpty("RLS policies must exist for RouteGeometries");
        policies.Should().Contain(p => p.PolicyName.StartsWith("RouteGeometriesTenantPolicy"));
        policies.Should().Contain(p => p.Qual != null, "a filter (USING) predicate must exist");
        policies.Should().Contain(p => p.WithCheck != null, "a block (WITH CHECK) predicate must exist");
    }

    private static RouteGeometryRepository MakeRepo(string connectionString, Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, Guid.NewGuid(), false);
        var factory = new NpgsqlConnectionFactory(connectionString, ctx);
        return new RouteGeometryRepository(factory);
    }

    [Fact]
    public async Task GetAsync_never_returns_another_tenants_row()
    {
        // The controller's cross-tenant test (RouteGeometryControllerTests) returns 403 at the
        // authorization layer before RouteGeometries is ever queried, so it can't catch an RLS
        // policy that silently failed to apply or was later dropped. This exercises the
        // repository directly (below the controller/service) to prove the RLS filter predicate
        // itself blocks a cross-tenant read at the data layer.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantA);
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantB);

        var tenantARepo = MakeRepo(fx.ConnectionString, tenantA);
        await tenantARepo.UpsertAsync(
            tenantA, routeId, stopSequenceHash: "hash-a", format: "google-encoded-polyline",
            encodedPolyline: "abc", distanceMeters: 100, durationSeconds: 60,
            provider: "google-routes", generatedAt: DateTime.UtcNow);

        // Sanity check: tenant A can read its own row.
        (await tenantARepo.GetAsync(routeId)).Should().NotBeNull();

        var tenantBRepo = MakeRepo(fx.ConnectionString, tenantB);
        var crossTenantRead = await tenantBRepo.GetAsync(routeId);

        crossTenantRead.Should().BeNull("RouteGeometriesTenantPolicy's filter predicate must hide tenant A's row from tenant B's session");
    }
}
