using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Dapper;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;
using FluentAssertions;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class TransportFleetHubTests(PostgresFixture fx)
{
    private const string Key = "test-signing-key-at-least-32-bytes-long!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static string IssueToken(Guid userId, Guid tenantId, string role)
    {
        var jwt = new JwtTokenService(new JwtOptions { SigningKey = Key }, new SystemClock());
        return jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
    }

    private static async Task<HubConnection> ConnectAsync(WebApplicationFactory<Program> app, string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{app.Server.BaseAddress}hubs/transport-fleet", opts =>
            {
                opts.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                opts.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task JoinBus_returns_true_for_the_duty_teacher_and_false_for_a_stranger()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.BusAssignments (Id, TenantId, TeacherUserId, BusId) VALUES (@Id, @TenantId, @TeacherUserId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, TeacherUserId = teacherId, BusId = busId });
        }

        await using var app = App();
        await using var teacherConn = await ConnectAsync(app, IssueToken(teacherId, tenantId, Policies.Teacher));
        await using var strangerConn = await ConnectAsync(app, IssueToken(strangerId, tenantId, Policies.Teacher));

        (await teacherConn.InvokeAsync<bool>("JoinBus", busId)).Should().BeTrue();
        (await strangerConn.InvokeAsync<bool>("JoinBus", busId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_denied_JoinBus_does_not_disconnect_the_connection()
    {
        var tenantId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync("INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-1')",
                new { Id = busId, TenantId = tenantId });
        }

        await using var app = App();
        await using var connection = await ConnectAsync(app, IssueToken(strangerId, tenantId, Policies.Teacher));

        (await connection.InvokeAsync<bool>("JoinBus", busId)).Should().BeFalse();
        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task JoinMyChildrenBuses_joins_only_buses_with_linked_children()
    {
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var otherParentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var busId = Guid.NewGuid();
        var otherBusId = Guid.NewGuid();

        await using (var conn = new NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-A')",
                new { Id = busId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Buses (Id, TenantId, BusNo) VALUES (@Id, @TenantId, 'BUS-B')",
                new { Id = otherBusId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.Students (Id, TenantId, Name, AdmissionNo, Status) VALUES (@Id, @TenantId, 'Kid', 'ADM-X', 'active')",
                new { Id = childId, TenantId = tenantId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = childId, BusId = busId });
            await conn.ExecuteAsync(
                "INSERT INTO dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@ParentUserId, @StudentId, @TenantId)",
                new { ParentUserId = parentId, StudentId = childId, TenantId = tenantId });
        }

        await using var app = App();
        await using var parentConn = await ConnectAsync(app, IssueToken(parentId, tenantId, Policies.StudentOrParent));
        await using var otherConn = await ConnectAsync(app, IssueToken(otherParentId, tenantId, Policies.StudentOrParent));

        var joined = await parentConn.InvokeAsync<IReadOnlyList<Guid>>("JoinMyChildrenBuses");
        joined.Should().ContainSingle().Which.Should().Be(busId);

        var otherJoined = await otherConn.InvokeAsync<IReadOnlyList<Guid>>("JoinMyChildrenBuses");
        otherJoined.Should().BeEmpty();
    }
}
