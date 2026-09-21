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
using Xunit;

namespace Sms.Tests.Integration.Operations;

[Collection("sql")]
public class OperationsSummaryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient Client(WebApplicationFactory<Program> app, Guid tenantId, params string[] roles)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, roles, isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

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
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    [Fact]
    public async Task Library_summary_derives_catalogue_issued_members_and_fines()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var dueDate = DateTime.UtcNow.Date.AddDays(-4).ToString("yyyy-MM-dd");

        // one issued+overdue book (contributes to issued, members, fines) …
        await Data(await principal.PostAsJsonAsync("/v1/library", new
        {
            title = "Overdue Title", author = "Author A", issued_to = "Alice", due_date = dueDate, status = "issued"
        }), HttpStatusCode.Created);
        // … and one available book (catalogue only)
        await Data(await principal.PostAsJsonAsync("/v1/library", new
        {
            title = "Shelf Title", author = "Author B", status = "available"
        }), HttpStatusCode.Created);

        var summary = await Data(await principal.GetAsync("/v1/library/summary"), HttpStatusCode.OK);
        summary.GetProperty("catalogue").GetInt32().Should().Be(2);
        summary.GetProperty("issued").GetInt32().Should().Be(1);
        summary.GetProperty("members").GetInt32().Should().Be(1);
        summary.GetProperty("fines_due").GetDecimal().Should().BeGreaterThan(0m, "an overdue issued book accrues a fine");
    }

    [Fact]
    public async Task Transport_summary_counts_vehicles_routes_stops_and_boarded_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var busId = Guid.NewGuid();
        var tripId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var student1Id = Guid.NewGuid();
        var student2Id = Guid.NewGuid();

        await Seed(fx.ConnectionString, tenantId, async conn =>
        {
            await conn.ExecuteAsync(
                "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
                new { Id = routeId, TenantId = tenantId, Name = "Route A" });
            // two buses sharing one named route
            await conn.ExecuteAsync(
                "INSERT dbo.Buses (Id, TenantId, BusNo, RouteName, RouteId) VALUES (@Id, @TenantId, @BusNo, @Route, @RouteId)",
                new[]
                {
                    new { Id = busId, TenantId = tenantId, BusNo = "BUS-1", Route = "Route A", RouteId = routeId },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, BusNo = "BUS-2", Route = "Route A", RouteId = routeId }
                });
            // three stops on the shared route
            await conn.ExecuteAsync(
                "INSERT dbo.RouteStops (Id, TenantId, RouteId, Name, Seq, Lat, Lng) VALUES (@Id, @TenantId, @RouteId, @Name, @Seq, 0, 0)",
                new[]
                {
                    new { Id = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId, Name = "Stop 1", Seq = 1 },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId, Name = "Stop 2", Seq = 2 },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, RouteId = routeId, Name = "Stop 3", Seq = 3 }
                });
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @A, @N)",
                new[]
                {
                    new { Id = student1Id, TenantId = tenantId, A = "TR-1", N = "Student One" },
                    new { Id = student2Id, TenantId = tenantId, A = "TR-2", N = "Student Two" }
                });
            await conn.ExecuteAsync(
                "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @StudentId, @BusId)",
                new[]
                {
                    new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = student1Id, BusId = busId },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, StudentId = student2Id, BusId = busId }
                });
            await conn.ExecuteAsync(
                "INSERT dbo.Trips (Id, TenantId, BusNo, Status, StartedAt) VALUES (@Id, @TenantId, @BusNo, 'live', @At)",
                new { Id = tripId, TenantId = tenantId, BusNo = "BUS-1", At = DateTime.UtcNow });
            await conn.ExecuteAsync(
                "INSERT dbo.Boardings (Id, TenantId, TripId, StudentId, State, At) VALUES (@Id, @TenantId, @TripId, @StudentId, 'boarded', @At)",
                new[]
                {
                    new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, StudentId = student1Id, At = DateTime.UtcNow },
                    new { Id = Guid.NewGuid(), TenantId = tenantId, TripId = tripId, StudentId = student2Id, At = DateTime.UtcNow }
                });
        });

        var principal = Client(app, tenantId, Policies.Principal);
        var summary = await Data(await principal.GetAsync("/v1/transport/summary"), HttpStatusCode.OK);
        summary.GetProperty("vehicles").GetInt32().Should().Be(2);
        summary.GetProperty("routes").GetInt32().Should().Be(1, "both buses share one named route");
        summary.GetProperty("stops").GetInt32().Should().Be(3);
        summary.GetProperty("students").GetInt32().Should().Be(2, "two pupils assigned to buses");
    }

    [Fact]
    public async Task Summaries_forbid_students()
    {
        await using var app = App();
        var student = Client(app, Guid.NewGuid(), Policies.StudentOrParent);

        (await student.GetAsync("/v1/library/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await student.GetAsync("/v1/transport/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
