using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Transport;

[Collection("sql")]
public class StudentBusTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId) =>
        Client(app, tenantId, Guid.NewGuid(), Policies.Principal);

    private static HttpClient ParentClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId) =>
        Client(app, tenantId, userId, Policies.StudentOrParent);

    private static HttpClient StudentClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId) =>
        Client(app, tenantId, userId, "student");

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @t::text, false)", new { t = tenantId });
        await work(conn);
    }

    /// Seeds a bus with a live trip and one recent GPS ping. Returns the (busId, busNo).
    private static async Task<(Guid busId, string busNo)> SeedLiveBus(
        NpgsqlConnection conn, Guid tenantId, string routeName, double lat, double lng)
    {
        var busId = Guid.NewGuid();
        var busNo = $"KA-{Guid.NewGuid():N}"[..12];
        var tripId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT dbo.Buses (Id, TenantId, BusNo, RouteName) VALUES (@Id, @TenantId, @BusNo, @RouteName)",
            new { Id = busId, TenantId = tenantId, BusNo = busNo, RouteName = routeName });
        await conn.ExecuteAsync(
            "INSERT dbo.Trips (Id, TenantId, BusId, BusNo, Status, StartedAt) " +
            "VALUES (@Id, @TenantId, @BusId, @BusNo, 'live', @StartedAt)",
            new { Id = tripId, TenantId = tenantId, BusId = busId, BusNo = busNo, StartedAt = DateTime.UtcNow });
        await conn.ExecuteAsync(
            "INSERT dbo.TripPings (Id, TenantId, TripId, Lat, Lng, SpeedKmh, Heading, At) " +
            "VALUES (@Id, @TenantId, @TripId, @Lat, @Lng, 30, 45, @At)",
            new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, Lat = lat, Lng = lng, At = DateTime.UtcNow });
        return (busId, busNo);
    }

    [Fact]
    public async Task Admin_assign_then_list_returns_student_on_bus()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = Guid.NewGuid();
        Guid busId = default;

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            (busId, _) = await SeedLiveBus(conn, tenantId, "Route A", 12.97, 77.59);
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantId, A = "ADM-1", N = "Alice Smith" });
        });

        var admin = AdminClient(app, tenantId);

        (await admin.PutAsJsonAsync($"/v1/transport/buses/{busId}/students/{studentId}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Data(await admin.GetAsync($"/v1/transport/buses/{busId}/students"), HttpStatusCode.OK);
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("student_name").GetString().Should().Be("Alice Smith");
        list[0].GetProperty("admission_no").GetString().Should().Be("ADM-1");
        list[0].GetProperty("initials").GetString().Should().Be("AS");
    }

    [Fact]
    public async Task Admin_assign_is_upsert_one_bus_per_student()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = Guid.NewGuid();
        Guid bus1 = default, bus2 = default;

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            (bus1, _) = await SeedLiveBus(conn, tenantId, "Route 1", 12.90, 77.50);
            (bus2, _) = await SeedLiveBus(conn, tenantId, "Route 2", 12.91, 77.51);
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantId, A = "ADM-2", N = "Bob Jones" });
        });

        var admin = AdminClient(app, tenantId);
        await admin.PutAsJsonAsync($"/v1/transport/buses/{bus1}/students/{studentId}", new { });
        await admin.PutAsJsonAsync($"/v1/transport/buses/{bus2}/students/{studentId}", new { });

        // Reassigned to bus2 only — bus1 roster is now empty.
        (await Data(await admin.GetAsync($"/v1/transport/buses/{bus1}/students"), HttpStatusCode.OK))
            .GetArrayLength().Should().Be(0);
        (await Data(await admin.GetAsync($"/v1/transport/buses/{bus2}/students"), HttpStatusCode.OK))
            .GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Admin_assign_unknown_bus_returns_404()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
            new { Id = studentId, TenantId = tenantId, A = "ADM-3", N = "Cara Lee" }));

        var admin = AdminClient(app, tenantId);
        (await admin.PutAsJsonAsync($"/v1/transport/buses/{Guid.NewGuid()}/students/{studentId}", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_endpoints_403_for_parent()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var parent = ParentClient(app, tenantId, Guid.NewGuid());
        (await parent.GetAsync($"/v1/transport/buses/{Guid.NewGuid()}/students"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Parent_sees_only_own_tenant_child_bus_despite_identical_admission_no()
    {
        await using var app = App();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var parentAUserId = Guid.NewGuid();
        const string sharedAdmission = "S001"; // both schools reuse this admission number

        string busNoA = "", busNoB = "";

        await Seed(fx.ConnectionString, tenantA, async conn =>
        {
            var (busId, busNo) = await SeedLiveBus(conn, tenantA, "A-Route", 12.97, 77.59);
            busNoA = busNo;
            var studentId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantA, A = sharedAdmission, N = "Child A" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantA, S = studentId, B = busId });
            // The parent account, linked to the child via Users.StudentId (= admission number).
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status) VALUES (@Id, @TenantId, @Adm, 0, 'active')",
                new { Id = parentAUserId, TenantId = tenantA, Adm = sharedAdmission });
        });

        await Seed(fx.ConnectionString, tenantB, async conn =>
        {
            var (busId, busNo) = await SeedLiveBus(conn, tenantB, "B-Route", 12.97, 77.59); // identical GPS on purpose
            busNoB = busNo;
            var studentId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantB, A = sharedAdmission, N = "Child B" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantB, S = studentId, B = busId });
        });

        var parentA = ParentClient(app, tenantA, parentAUserId);
        var data = await Data(await parentA.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);

        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("student_name").GetString().Should().Be("Child A");
        data[0].GetProperty("bus_no").GetString().Should().Be(busNoA);
        data[0].GetProperty("bus_no").GetString().Should().NotBe(busNoB);
        data[0].GetProperty("route_name").GetString().Should().Be("A-Route");
    }

    [Fact]
    public async Task Parent_live_bus_includes_assigned_student_stop_and_route_stops()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var parentUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var stopA = Guid.NewGuid();
        var stopB = Guid.NewGuid();
        string busNo = "";

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            var (busId, no) = await SeedLiveBus(conn, tenantId, "Morning Route", 12.97, 77.59);
            busNo = no;
            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "Morning Route" });
            await conn.ExecuteAsync(
                "UPDATE dbo.Buses SET RouteId = @RouteId WHERE Id = @BusId",
                new { RouteId = routeId, BusId = busId });
            await conn.ExecuteAsync(
                "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, @Lat, @Lng)",
                new { Id = stopA, TenantId = tenantId, RouteId = routeId, Name = "Oak Gate", Seq = 1, Lat = 12.96, Lng = 77.58 });
            await conn.ExecuteAsync(
                "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, @Lat, @Lng)",
                new { Id = stopB, TenantId = tenantId, RouteId = routeId, Name = "Maple Stop", Seq = 2, Lat = 12.971, Lng = 77.591 });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = studentId, TenantId = tenantId, A = "ADM-STOP", N = "Rahul Sharma" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId, RouteId, StopId) VALUES (@Id, @TenantId, @S, @B, @R, @Stop)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = studentId, B = busId, R = routeId, Stop = stopB });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status) VALUES (@Id, @TenantId, @Adm, 0, 'active')",
                new { Id = parentUserId, TenantId = tenantId, Adm = "ADM-STOP" });
        });

        var parent = ParentClient(app, tenantId, parentUserId);
        var data = await Data(await parent.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("bus_no").GetString().Should().Be(busNo);
        data[0].GetProperty("student_stop_id").GetGuid().Should().Be(stopB);
        data[0].GetProperty("student_stop_name").GetString().Should().Be("Maple Stop");
        data[0].GetProperty("route_stops").GetArrayLength().Should().Be(2);
        data[0].GetProperty("route_stops")[1].GetProperty("name").GetString().Should().Be("Maple Stop");
    }

    [Fact]
    public async Task Parent_gets_unassigned_row_when_child_has_no_bus()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var parentUserId = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, A = "S777", N = "Lonely Child" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, IsPlatform, Status) VALUES (@Id, @TenantId, @Adm, 0, 'active')",
                new { Id = parentUserId, TenantId = tenantId, Adm = "S777" });
        });

        var parent = ParentClient(app, tenantId, parentUserId);
        var data = await Data(await parent.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("student_name").GetString().Should().Be("Lonely Child");
        data[0].GetProperty("assignment").GetString().Should().Be("none");
        data[0].GetProperty("tracking_status").GetString().Should().Be("OFFLINE");
        data[0].GetProperty("bus_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Parent_with_ParentStudentLinks_sees_each_childs_own_bus()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var parentUserId = Guid.NewGuid();
        var childA = Guid.NewGuid();
        var childB = Guid.NewGuid();
        string busNoA = "", busNoB = "";

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            var (busA, noA) = await SeedLiveBus(conn, tenantId, "Route A", 12.97, 77.59);
            var (busB, noB) = await SeedLiveBus(conn, tenantId, "Route B", 13.01, 77.61);
            busNoA = noA;
            busNoB = noB;
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section) VALUES (@Id, @TenantId, @A, @N, @G, @S)",
                new { Id = childA, TenantId = tenantId, A = "ADM-A", N = "Aarav Sharma", G = "I", S = "A" });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section) VALUES (@Id, @TenantId, @A, @N, @G, @S)",
                new { Id = childB, TenantId = tenantId, A = "ADM-B", N = "Ananya Sharma", G = "VI", S = "B" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = childA, B = busA });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = childB, B = busB });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, IsPlatform, Status) VALUES (@Id, @TenantId, @Email, 0, 'active')",
                new { Id = parentUserId, TenantId = tenantId, Email = $"p-{parentUserId:N}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = parentUserId, S = childA, T = tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = parentUserId, S = childB, T = tenantId });
            await conn.ExecuteAsync("UPDATE dbo.Buses SET Driver = N'Raj Kumar' WHERE Id = @Id", new { Id = busA });
        });

        var parent = ParentClient(app, tenantId, parentUserId);
        var data = await Data(await parent.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(2);
        var aarav = Enumerable.Range(0, 2).Select(i => data[i])
            .First(r => r.GetProperty("student_name").GetString() == "Aarav Sharma");
        var ananya = Enumerable.Range(0, 2).Select(i => data[i])
            .First(r => r.GetProperty("student_name").GetString() == "Ananya Sharma");
        aarav.GetProperty("bus_no").GetString().Should().Be(busNoA);
        ananya.GetProperty("bus_no").GetString().Should().Be(busNoB);
        aarav.GetProperty("bus_no").GetString().Should().NotBe(ananya.GetProperty("bus_no").GetString());
        aarav.GetProperty("tracking_status").GetString().Should().Be("LIVE");
        aarav.GetProperty("driver").GetString().Should().Be("Raj Kumar");
        aarav.GetProperty("grade").GetString().Should().Be("I");
        aarav.GetProperty("assignment").GetString().Should().Be("assigned");
    }

    [Fact]
    public async Task Parent_cannot_read_another_parents_child_bus_by_tampering()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        var childB = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            var (busId, _) = await SeedLiveBus(conn, tenantId, "Secret Route", 12.9, 77.5);
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = childB, TenantId = tenantId, A = "ADM-B-ONLY", N = "Other Kid" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = childB, B = busId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, IsPlatform, Status) VALUES (@Id, @TenantId, @Email, 0, 'active')",
                new { Id = parentA, TenantId = tenantId, Email = $"a-{parentA:N}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, IsPlatform, Status) VALUES (@Id, @TenantId, @Email, 0, 'active')",
                new { Id = parentB, TenantId = tenantId, Email = $"b-{parentB:N}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = parentB, S = childB, T = tenantId });
        });

        var caller = ParentClient(app, tenantId, parentA);
        var data = await Data(await caller.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(0);
        (await caller.GetAsync($"/v1/bus/{Guid.NewGuid()}/position")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Student_sees_only_own_bus_not_a_siblings()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var studentUserId = Guid.NewGuid();
        var parentUserId = Guid.NewGuid();
        var me = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        string myBusNo = "";

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            var (myBus, no) = await SeedLiveBus(conn, tenantId, "My Route", 12.97, 77.59);
            var (sibBus, _) = await SeedLiveBus(conn, tenantId, "Sibling Route", 13.01, 77.61);
            myBusNo = no;
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = me, TenantId = tenantId, A = "STU-ME", N = "Cube" });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new { Id = sibling, TenantId = tenantId, A = "STU-SIB", N = "Sibling" });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = me, B = myBus });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
                new { Id = Guid.NewGuid(), TenantId = tenantId, S = sibling, B = sibBus });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, StudentId, Email, IsPlatform, Status) VALUES (@Id, @TenantId, @Adm, @Email, 0, 'active')",
                new { Id = studentUserId, TenantId = tenantId, Adm = "STU-ME", Email = $"stu-{studentUserId:N}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Email, IsPlatform, Status) VALUES (@Id, @TenantId, @Email, 0, 'active')",
                new { Id = parentUserId, TenantId = tenantId, Email = $"p-{parentUserId:N}@test.local" });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = parentUserId, S = me, T = tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = parentUserId, S = sibling, T = tenantId });
            // Even if this student user id is wrongly linked to a sibling, selfOnly must pin to STU-ME.
            await conn.ExecuteAsync(
                "INSERT dbo.ParentStudentLinks (ParentUserId, StudentId, TenantId) VALUES (@P, @S, @T)",
                new { P = studentUserId, S = sibling, T = tenantId });
        });

        var student = StudentClient(app, tenantId, studentUserId);
        var data = await Data(await student.GetAsync("/v1/me/children/bus"), HttpStatusCode.OK);
        data.GetArrayLength().Should().Be(1);
        data[0].GetProperty("student_name").GetString().Should().Be("Cube");
        data[0].GetProperty("bus_no").GetString().Should().Be(myBusNo);
        data[0].GetProperty("admission_no").GetString().Should().Be("STU-ME");
    }
}
