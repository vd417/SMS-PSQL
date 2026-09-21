using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Application.Services.AiSearch.Handlers;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// Exercises BusLocationSearchHandler directly (no HTTP layer needed yet). This is a one-shot
/// snapshot handler — it delegates entirely to IStudentBusService.GetMyChildrenBusAsync, which is
/// already scoped to the caller's own linked children via Users.StudentId, so the handler itself has
/// no student-id filtering logic of its own to test beyond the empty/non-empty response shape.
[Collection("sql")]
public class BusLocationSearchHandlerTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", "integration-test-signing-key-32-bytes-min!!");
        });

    private static async Task<AiSearchResponse> Handle(
        WebApplicationFactory<Program> app, Guid tenantId, Guid parentUserId)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(tenantId, parentUserId, isPlatform: false);
        var handler = new BusLocationSearchHandler(
            scope.ServiceProvider.GetRequiredService<IStudentBusService>(),
            scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>());
        var auth = new AiAuthorizationResult(
            Allowed: true, ResultIntent: "BusLocationSearch", ResolvedStudentId: null,
            AllowedChildStudentIds: null, AllowedClassNames: null,
            ClampedFilters: new AiSearchFilters(null, null, null, null, false),
            Unrestricted: false, NameUnmatched: false);
        return await handler.HandleAsync(auth, "en", 1, 20);
    }

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private static async Task InsertStudent(
        NpgsqlConnection conn, Guid id, Guid tenantId, string admissionNo, string name) =>
        await conn.ExecuteAsync(
            """
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name)
            VALUES (@id, @tenantId, @admissionNo, @name)
            """,
            new { id, tenantId, admissionNo, name });

    private static async Task<Guid> InsertParentUser(
        NpgsqlConnection conn, Guid tenantId, string admissionNo)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT dbo.Users (Id, TenantId, Email, Phone, IsPlatform, Status, StudentId, MustSetPassword, Name)
            VALUES (@id, @tenantId, @email, NULL, 0, N'active', @admissionNo, 1, N'Parent')
            """,
            new { id, tenantId, email = $"parent{Guid.NewGuid():N}@home.test", admissionNo });
        return id;
    }

    private static async Task<Guid> InsertBus(NpgsqlConnection conn, Guid tenantId, string busNo)
    {
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT dbo.Buses (Id, TenantId, BusNo)
            VALUES (@id, @tenantId, @busNo)
            """,
            new { id, tenantId, busNo });
        return id;
    }

    private static async Task AssignBus(NpgsqlConnection conn, Guid tenantId, Guid studentId, Guid busId) =>
        await conn.ExecuteAsync(
            """
            INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId)
            VALUES (NEWID(), @tenantId, @studentId, @busId)
            """,
            new { tenantId, studentId, busId });

    [Fact]
    public async Task Parent_with_no_bus_assigned_gets_no_match_not_an_error()
    {
        var tenantId = Guid.NewGuid();
        var admissionNo = $"ADM-NB-{Guid.NewGuid():N}"[..20];
        Guid parentUserId = default;

        await Seed(async conn =>
        {
            var studentId = Guid.NewGuid();
            await InsertStudent(conn, studentId, tenantId, admissionNo, "No Bus Kid");
            parentUserId = await InsertParentUser(conn, tenantId, admissionNo);
        });

        await using var app = App();
        var response = await Handle(app, tenantId, parentUserId);

        response.Intent.Should().Be("Unsupported");
        response.Data.Should().BeNull();

        using var scope = app.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<IAiAnswerTemplateService>();
        response.Answer.Should().Be(templates.RenderNoMatch("en"));
    }

    [Fact]
    public async Task Parent_only_ever_sees_their_own_childs_bus_never_another_parents()
    {
        var tenantId = Guid.NewGuid();
        var admissionA = $"ADM-CA-{Guid.NewGuid():N}"[..20];
        var admissionB = $"ADM-CB-{Guid.NewGuid():N}"[..20];
        Guid parentAUserId = default, busAId = default, busBId = default;

        await Seed(async conn =>
        {
            var studentA = Guid.NewGuid();
            var studentB = Guid.NewGuid();
            await InsertStudent(conn, studentA, tenantId, admissionA, "Child A");
            await InsertStudent(conn, studentB, tenantId, admissionB, "Child B");

            parentAUserId = await InsertParentUser(conn, tenantId, admissionA);
            await InsertParentUser(conn, tenantId, admissionB);

            busAId = await InsertBus(conn, tenantId, $"BUS-A-{Guid.NewGuid():N}"[..10]);
            busBId = await InsertBus(conn, tenantId, $"BUS-B-{Guid.NewGuid():N}"[..10]);

            await AssignBus(conn, tenantId, studentA, busAId);
            await AssignBus(conn, tenantId, studentB, busBId);
        });

        await using var app = App();
        var response = await Handle(app, tenantId, parentAUserId);

        response.Intent.Should().Be("BusLocationSearch");
        response.Count.Should().Be(1);
        var rows = (IReadOnlyList<ChildBusPositionResponse>)response.Data!;
        rows.Should().ContainSingle();
        rows[0].BusId.Should().Be(busAId);
        rows[0].BusId.Should().NotBe(busBId);
    }
}
