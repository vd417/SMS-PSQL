using Dapper;
using Microsoft.Data.SqlClient;
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
        await using var conn = new SqlConnection(fx.ConnectionString);
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
        // Column-existence alone (above) says nothing about whether M0207's
        // CREATE SECURITY POLICY statement actually applied, or whether it was later dropped.
        // Query sys.security_policies/sys.security_predicates directly, the same way SQL Server's
        // own catalog would be inspected to audit RLS coverage in production.
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();

        var policy = await conn.QuerySingleOrDefaultAsync<(string Name, bool IsEnabled)?>(
            @"SELECT p.name AS Name, p.is_enabled AS IsEnabled
              FROM sys.security_policies p
              WHERE p.name = 'RouteGeometriesTenantPolicy'");

        policy.Should().NotBeNull("M0207 must create rls.RouteGeometriesTenantPolicy");
        policy!.Value.IsEnabled.Should().BeTrue("the policy must be WITH (STATE = ON)");

        var predicates = (await conn.QueryAsync<(string PredicateType, string TargetObject)>(
            @"SELECT sp.predicate_type_desc AS PredicateType, OBJECT_NAME(sp.target_object_id) AS TargetObject
              FROM sys.security_predicates sp
              JOIN sys.security_policies pol ON pol.object_id = sp.object_id
              WHERE pol.name = 'RouteGeometriesTenantPolicy'")).ToList();

        predicates.Should().Contain(p => p.TargetObject == "RouteGeometries" && p.PredicateType == "FILTER");
        predicates.Should().Contain(p => p.TargetObject == "RouteGeometries" && p.PredicateType == "BLOCK");
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
