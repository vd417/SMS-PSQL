using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using FluentAssertions;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TransportAuthorizationRepositoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    [Fact]
    public async Task HasChildOnBusAsync_true_only_for_the_students_own_bus()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        const string admissionNo = "ADM-TEST-001";

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Students\" (\"Id\", \"TenantId\", \"Name\", \"AdmissionNo\") VALUES (@Id, @TenantId, 'Test Student', @AdmissionNo)",
                new { Id = studentId, TenantId = tenantId, AdmissionNo = admissionNo });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"StudentBusAssignments\" (\"Id\", \"TenantId\", \"StudentId\", \"BusId\") VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = studentId, BusId = busId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<StudentBusRepository>();

        (await repo.HasChildOnBusAsync(admissionNo, busId, default)).Should().BeTrue();
        (await repo.HasChildOnBusAsync(admissionNo, otherBusId, default)).Should().BeFalse();
        (await repo.HasChildOnBusAsync("NO-SUCH-ADMISSION", busId, default)).Should().BeFalse();
        (await repo.HasLinkedChildOnBusAsync(Guid.NewGuid(), busId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task IsDutyTeacherForBusAsync_true_only_for_the_assigned_teacher()
    {
        var tenantId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var otherTeacherId = Guid.NewGuid();
        var busId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"BusAssignments\" (\"Id\", \"TenantId\", \"TeacherUserId\", \"BusId\") VALUES (@Id, @TenantId, @TeacherUserId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TeacherUserId = teacherId, BusId = busId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.IsDutyTeacherForBusAsync(teacherId, busId, default)).Should().BeTrue();
        (await repo.IsDutyTeacherForBusAsync(otherTeacherId, busId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task IsTravelingTeacherForBusAsync_true_only_for_added_teachers_and_supports_multiple_teachers_per_bus()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var secondTeacherId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<BusRepository>();

        (await repo.IsTravelingTeacherForBusAsync(teacherId, busId, default)).Should().BeFalse();

        await repo.AddTravelingTeacherAsync(tenantId, busId, teacherId, default);
        await repo.AddTravelingTeacherAsync(tenantId, busId, secondTeacherId, default);

        (await repo.IsTravelingTeacherForBusAsync(teacherId, busId, default)).Should().BeTrue();
        (await repo.IsTravelingTeacherForBusAsync(secondTeacherId, busId, default)).Should().BeTrue();
        (await repo.IsTravelingTeacherForBusAsync(strangerId, busId, default)).Should().BeFalse();

        await repo.RemoveTravelingTeacherAsync(tenantId, busId, teacherId, default);
        (await repo.IsTravelingTeacherForBusAsync(teacherId, busId, default)).Should().BeFalse();
        (await repo.IsTravelingTeacherForBusAsync(secondTeacherId, busId, default)).Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveDriverOrConductorRoleByBusAsync_and_GetBusIdAsync()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var conductorId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var tripId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO \"dbo\".\"Buses\" (\"Id\", \"TenantId\", \"BusNo\") VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                @"INSERT INTO ""dbo"".""Trips"" (""Id"", ""TenantId"", ""BusId"", ""DriverId"", ""ConductorId"", ""Direction"", ""Status"", ""StartedAt"")
                  VALUES (@Id, @TenantId, @BusId, @DriverId, @ConductorId, 'pickup', 'live', now())",
                new { Id = tripId, TenantId = tenantId, BusId = busId, DriverId = driverId, ConductorId = conductorId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(tenantId, null, isPlatform: false);
        var repo = scope.ServiceProvider.GetRequiredService<TripRepository>();

        (await repo.GetActiveDriverOrConductorRoleByBusAsync(busId, driverId, default)).Should().Be("driver");
        (await repo.GetActiveDriverOrConductorRoleByBusAsync(busId, conductorId, default)).Should().Be("conductor");
        (await repo.GetActiveDriverOrConductorRoleByBusAsync(busId, strangerId, default)).Should().BeNull();
        (await repo.GetBusIdAsync(tripId, default)).Should().Be(busId);
        (await repo.GetBusIdAsync(Guid.NewGuid(), default)).Should().BeNull();
    }
}
