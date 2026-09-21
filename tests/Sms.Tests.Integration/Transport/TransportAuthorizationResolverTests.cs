using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Sms.Application.Interfaces.DAO;
using Sms.Application.Services.Transport;
using Sms.Shared.Kernel.Authz;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TransportAuthorizationResolverTests(PostgresFixture fx)
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
    public async Task Principal_can_view_any_bus_in_their_own_tenant_but_not_another_tenants()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var principalId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(principalId, tenantId, [Policies.Principal], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(principalId, otherTenantId, [Policies.Principal], busId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Teacher_can_view_only_their_assigned_duty_bus()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.BusAssignments (Id, TenantId, TeacherUserId, BusId) VALUES (@Id, @TenantId, @TeacherUserId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TeacherUserId = teacherId, BusId = busId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(teacherId, tenantId, [Policies.Teacher], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(teacherId, tenantId, [Policies.Teacher], otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Teacher_can_view_a_bus_they_are_a_traveling_teacher_on_even_without_a_duty_assignment()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            // Deliberately NOT inserted into dbo.BusAssignments (no duty assignment) —
            // access here must come solely from the traveling-teacher grant.
            await conn.ExecuteAsync(
                "INSERT INTO dbo.BusTravelingTeachers (Id, TenantId, BusId, TeacherUserId) VALUES (@Id, @TenantId, @BusId, @TeacherUserId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, BusId = busId, TeacherUserId = teacherId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(teacherId, tenantId, [Policies.Teacher], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(teacherId, tenantId, [Policies.Teacher], otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Driver_can_view_only_the_bus_of_their_own_active_trip()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                @"INSERT INTO dbo.Trips (Id, TenantId, BusId, DriverId, Direction, Status, StartedAt)
                  VALUES (@Id, @TenantId, @BusId, @DriverId, 'pickup', 'live', SYSUTCDATETIME())",
                new { Id = Guid.NewGuid(), TenantId = tenantId, BusId = busId, DriverId = driverId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(driverId, tenantId, [Policies.Driver], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(driverId, tenantId, [Policies.Driver], otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Parent_can_view_only_their_own_childs_bus()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        const string admissionNo = "ADM-PARENT-001";

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Students (Id, TenantId, Name, AdmissionNo) VALUES (@Id, @TenantId, 'Kid', @AdmissionNo)",
                new { Id = studentId, TenantId = tenantId, AdmissionNo = admissionNo });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = studentId, BusId = busId });
            // The parent's Users row is linked to their child via Users.StudentId = admission number
            // (see StudentBusService.GetMyChildrenBusAsync for this same pattern). dbo.Users (see
            // M0001_Foundation_Tables) has no NOT NULL columns besides its defaulted Id/IsPlatform/
            // Status/CreatedAt, and no Role column at all (roles live in dbo.UserRoles) — so only
            // Id/TenantId/StudentId/Email need to be supplied here.
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, StudentId, Email) VALUES (@Id, @TenantId, @AdmissionNo, @Email)",
                new { Id = parentId, TenantId = tenantId, AdmissionNo = admissionNo, Email = $"parent-{parentId}@test.local" });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(parentId, tenantId, [Policies.StudentOrParent], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(parentId, tenantId, [Policies.StudentOrParent], otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Parent_linked_only_via_ParentStudentLinks_can_view_childs_bus()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Students (Id, TenantId, Name, AdmissionNo) VALUES (@Id, @TenantId, 'Kid', 'ADM-LINK-001')",
                new { Id = studentId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = studentId, BusId = busId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, Email) VALUES (@Id, @TenantId, @Email)",
                new { Id = parentId, TenantId = tenantId, Email = $"link-parent-{parentId}@test.local" });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@ParentUserId, @StudentId, @TenantId)",
                new { ParentUserId = parentId, StudentId = studentId, TenantId = tenantId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(parentId, tenantId, [Policies.StudentOrParent], busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(parentId, tenantId, [Policies.StudentOrParent], otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Staff_role_holder_is_granted_or_denied_based_on_duty_teacher_and_active_driver_checks()
    {
        // The real staff auto-provisioning flow (Staff_EnsureLogin.sql) inserts exactly one role
        // for every staff member: literally 'staff' into dbo.UserRoles — it never inserts
        // "school.teacher", "driver", or "conductor". And AuthService issues the JWT's role claims
        // straight from IAuthDao.GetRolesAsync (see AuthService.RefreshAsync/VerifyOtpAsync etc:
        // `var roles = await users.GetRolesAsync(user.Id, ct); var access = jwt.IssueAccess(..., roles, ...)`).
        // So this test derives callerRoles the same way production does: seed 'staff' in
        // dbo.UserRoles exactly as the real proc does, then call the same IAuthDao.GetRolesAsync
        // used at login to obtain the roles collection, instead of passing Policies.Teacher/Driver
        // directly — this is what genuinely exercises "a real driver/teacher/conductor, whose JWT
        // only ever carries 'staff', is not wrongly denied".
        var tenantId = Guid.NewGuid();
        var dutyBusId = Guid.NewGuid();
        var drivenBusId = Guid.NewGuid();
        var uninvolvedBusId = Guid.NewGuid();
        var teacherStaffId = Guid.NewGuid();
        var driverStaffId = Guid.NewGuid();
        var uninvolvedStaffId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@DutyBusId, @TenantId, 'BUS-1'), (@DrivenBusId, @TenantId, 'BUS-2'), (@UninvolvedBusId, @TenantId, 'BUS-3')",
                new { DutyBusId = dutyBusId, DrivenBusId = drivenBusId, UninvolvedBusId = uninvolvedBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.BusAssignments (Id, TenantId, TeacherUserId, BusId) VALUES (@Id, @TenantId, @TeacherUserId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TeacherUserId = teacherStaffId, BusId = dutyBusId });
            await conn.ExecuteAsync(
                @"INSERT INTO dbo.Trips (Id, TenantId, BusId, DriverId, Direction, Status, StartedAt)
                  VALUES (@Id, @TenantId, @BusId, @DriverId, 'pickup', 'live', SYSUTCDATETIME())",
                new { Id = Guid.NewGuid(), TenantId = tenantId, BusId = drivenBusId, DriverId = driverStaffId });

            // Mirror exactly what Staff_EnsureLogin.sql does: `INSERT dbo.UserRoles (UserId, Role) VALUES (@UserId, N'staff')`.
            foreach (var staffId in new[] { teacherStaffId, driverStaffId, uninvolvedStaffId })
                await conn.ExecuteAsync("INSERT INTO dbo.UserRoles (UserId, Role) VALUES (@UserId, N'staff')", new { UserId = staffId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();
        var authDao = scope.ServiceProvider.GetRequiredService<IAuthDao>();

        var teacherRoles = await authDao.GetRolesAsync(teacherStaffId);
        var driverRoles = await authDao.GetRolesAsync(driverStaffId);
        var uninvolvedRoles = await authDao.GetRolesAsync(uninvolvedStaffId);
        teacherRoles.Should().BeEquivalentTo(["staff"]);
        driverRoles.Should().BeEquivalentTo(["staff"]);
        uninvolvedRoles.Should().BeEquivalentTo(["staff"]);

        (await resolver.CanViewBusAsync(teacherStaffId, tenantId, teacherRoles, dutyBusId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(driverStaffId, tenantId, driverRoles, drivenBusId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(uninvolvedStaffId, tenantId, uninvolvedRoles, dutyBusId, default)).Should().BeFalse();
        (await resolver.CanViewBusAsync(uninvolvedStaffId, tenantId, uninvolvedRoles, drivenBusId, default)).Should().BeFalse();
        (await resolver.CanViewBusAsync(uninvolvedStaffId, tenantId, uninvolvedRoles, uninvolvedBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Caller_with_multiple_roles_is_granted_access_via_any_role_even_if_the_first_checked_role_fails()
    {
        // Proves the fix for the multi-role early-return bug: a caller holding BOTH
        // Policies.Teacher and Policies.StudentOrParent, who is NOT the duty teacher for this bus
        // (so the teacher branch's DB check fails) but IS the parent of a child assigned to this
        // bus, must still be granted access via the parent branch — i.e. the branches must be
        // tried as a union of grants, not "stop at the first role that matches".
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        const string admissionNo = "ADM-MULTIROLE-001";

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1'), (@OtherId, @TenantId, 'BUS-2')",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId });
            // Caller is deliberately NOT inserted into dbo.BusAssignments as duty teacher for `busId`
            // — the teacher branch's IsDutyTeacherForBusAsync check must fail for this caller.
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Students (Id, TenantId, Name, AdmissionNo) VALUES (@Id, @TenantId, 'Kid', @AdmissionNo)",
                new { Id = studentId, TenantId = tenantId, AdmissionNo = admissionNo });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = studentId, BusId = busId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Users (Id, TenantId, StudentId, Email) VALUES (@Id, @TenantId, @AdmissionNo, @Email)",
                new { Id = callerId, TenantId = tenantId, AdmissionNo = admissionNo, Email = $"multirole-{callerId}@test.local" });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        string[] callerRoles = [Policies.Teacher, Policies.StudentOrParent];

        (await resolver.CanViewBusAsync(callerId, tenantId, callerRoles, busId, default)).Should().BeTrue();
        (await resolver.CanViewBusAsync(callerId, tenantId, callerRoles, otherBusId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_role_is_denied()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
        }
        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewBusAsync(Guid.NewGuid(), tenantId, ["some.other.role"], busId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanViewRouteAsync_admin_sees_route_with_a_bus_in_their_own_tenant_but_not_another_tenants()
    {
        // This exercises admin visibility via the per-bus CanViewBusAsync fan-out (the route has a
        // bus, so the admin's own admin-fast-path branch inside CanViewBusAsync grants it there).
        // See CanViewRouteAsync_admin_sees_a_route_with_zero_buses_assigned_in_their_own_tenant below
        // for the route-level admin fast-path that additionally exists for zero-bus routes.
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var principalId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, 'ROUTE-1')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo, RouteId) VALUES (@Id, @TenantId, 'BUS-1', @RouteId)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewRouteAsync(principalId, tenantId, [Policies.Principal], routeId, default)).Should().BeTrue();
        (await resolver.CanViewRouteAsync(principalId, otherTenantId, [Policies.Principal], routeId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanViewRouteAsync_teacher_sees_route_via_a_bus_they_have_duty_on()
    {
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var otherRouteId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, 'ROUTE-1'), (@OtherId, @TenantId, 'ROUTE-2')",
                new { Id = routeId, OtherId = otherRouteId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo, RouteId) VALUES (@Id, @TenantId, 'BUS-1', @RouteId), (@OtherId, @TenantId, 'BUS-2', @OtherRouteId)",
                new { Id = busId, OtherId = otherBusId, TenantId = tenantId, RouteId = routeId, OtherRouteId = otherRouteId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.BusAssignments (Id, TenantId, TeacherUserId, BusId) VALUES (@Id, @TenantId, @TeacherUserId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TeacherUserId = teacherId, BusId = busId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewRouteAsync(teacherId, tenantId, [Policies.Teacher], routeId, default)).Should().BeTrue();
        (await resolver.CanViewRouteAsync(teacherId, tenantId, [Policies.Teacher], otherRouteId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanViewRouteAsync_returns_false_when_caller_has_no_relationship_to_any_bus_on_the_route()
    {
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var uninvolvedTeacherId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, 'ROUTE-1')",
                new { Id = routeId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo, RouteId) VALUES (@Id, @TenantId, 'BUS-1', @RouteId)",
                new { Id = busId, TenantId = tenantId, RouteId = routeId });
            // Deliberately no BusAssignments/BusTravelingTeachers/Trips/StudentBusAssignments row
            // links this teacher to the one bus on the route — they must be denied.
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewRouteAsync(uninvolvedTeacherId, tenantId, [Policies.Teacher], routeId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanViewRouteAsync_returns_false_for_a_route_with_no_buses_assigned()
    {
        var tenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, 'ROUTE-1')",
                new { Id = routeId, TenantId = tenantId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewRouteAsync(Guid.NewGuid(), tenantId, [Policies.Teacher], routeId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task CanViewRouteAsync_admin_sees_a_route_with_zero_buses_assigned_in_their_own_tenant()
    {
        // Route-level admin fast-path: an admin building out a route in the route-builder flow
        // (adding stops before any bus is ever assigned) must still be able to see their own
        // tenant's route even though the per-bus fan-out has nothing to iterate over yet. Mirrors
        // CanViewBusAsync's own admin-fast-path branch (existence check scoped by RLS tenant context)
        // rather than duplicating role logic.
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var principalId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, 'ROUTE-1')",
                new { Id = routeId, TenantId = tenantId });
        }

        await using var app = App();
        using var scope = app.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITransportAuthorizationResolver>();

        (await resolver.CanViewRouteAsync(principalId, tenantId, [Policies.Principal], routeId, default)).Should().BeTrue();
        (await resolver.CanViewRouteAsync(principalId, otherTenantId, [Policies.Principal], routeId, default)).Should().BeFalse();
    }
}
