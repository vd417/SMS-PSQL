using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Sis;

/// Covers FeeService.ReconcileFeesForClassAsync — the explicit "Sync/Apply Existing Fees" action
/// for students who ALREADY existed before the on-create auto-apply hooks ever ran for them
/// (Aarav's real case: created by a backend build that predated this feature). Students here are
/// inserted directly via SQL, deliberately bypassing every application code path, to genuinely
/// simulate "already in the database, no backfill ever attempted" rather than re-testing the
/// on-create hooks (that's FeeAutoApplyOnCreateTests's job).
[Collection("sql")]
public class FeeReconciliationTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string AcademicYear = "2026-27";
    private const string ClassLabel = "I-A";
    private const int OldStudentCount = 500;

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient PrincipalClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.principal"], isPlatform: false);
        var client = app.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage res, HttpStatusCode expected)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, because: body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Guid> CreateTransportHeadAsync(HttpClient client, string name)
    {
        var head = await Data(await client.PostAsJsonAsync("/v1/fees/heads", new
        {
            name,
            is_transport_fee_head = true,
        }), HttpStatusCode.Created);
        return head.GetProperty("id").GetGuid();
    }

    private static async Task UpsertStructureAsync(HttpClient client, string amountsJson)
    {
        await Data(await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            name = "AY fees",
            academic_year = AcademicYear,
            currency = "INR",
            effective_from = "2026-04-01",
            status = "active",
            amounts_json = amountsJson,
        }), HttpStatusCode.OK);
    }

    private static async Task<int> GenerateInvoicesAsync(HttpClient client, string term)
    {
        var result = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = AcademicYear,
            term,
            classes = new[] { ClassLabel },
        }), HttpStatusCode.OK);
        return result.GetProperty("created").GetInt32();
    }

    private static async Task<int> ReconcileAsync(HttpClient client, string classLabel)
    {
        var result = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/reconcile", new
        {
            classes = new[] { classLabel },
        }), HttpStatusCode.OK);
        return result.GetProperty("created").GetInt32();
    }

    /// Establishes a real seed student, opted into transport, the ordinary way — the ADMIN then
    /// runs Generate Invoices per period against THIS one student to establish each period, one
    /// at a time (kept to a single seed student deliberately: a second seed student would itself
    /// get auto-backfilled onto any period that already exists the moment its own transport is
    /// set, which is correct new behavior but would make the "created" counts below ambiguous).
    private static async Task<Guid> CreateSeedStudentAsync(HttpClient client, Guid routeId, Guid transportHeadId)
    {
        var student = await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = "SEED-1",
            name = "Seed Kid",
            grade = "I",
            section = "A",
            roll = 1,
            guardian_name = "Seed Parent",
            guardian_phone = "9000000000",
            email = "seed@example.com",
            gender = "M",
            dob = "2019-04-23",
        }), HttpStatusCode.Created);
        var studentId = student.GetProperty("id").GetGuid();

        await Data(await client.PutAsJsonAsync($"/v1/students/{studentId}/transport", new
        {
            opted_in = true,
            route_id = routeId,
            fee_head_id = transportHeadId,
        }), HttpStatusCode.OK);

        return studentId;
    }

    private sealed record InvoiceRow(Guid Id, Guid StudentId, string Period, decimal Amount);

    private static async Task<IReadOnlyList<InvoiceRow>> ListInvoicesAsync(
        string cs, Guid tenantId, IReadOnlyList<Guid> studentIds)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        var rows = await conn.QueryAsync<InvoiceRow>(
            "SELECT Id, StudentId, Period, Amount FROM dbo.FeeInvoices WHERE StudentId IN @ids",
            new { ids = studentIds });
        return rows.ToList();
    }

    /// Inserts one "already existed before this feature shipped" student directly via SQL —
    /// deliberately bypassing CreateStudentAsync/StudentTransportService entirely, so no
    /// application-level fee hook has EVER run for it, matching Aarav's real situation.
    private static async Task<Guid> InsertPreExistingStudentAsync(
        string cs, Guid tenantId, string admissionNo, bool optedIn, Guid? routeId, Guid? feeHeadId)
    {
        var id = Guid.NewGuid();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            """
            INSERT dbo.Students
                (Id, TenantId, AdmissionNo, Name, Gender, Grade, Section, ClassLabel, Roll,
                 GuardianName, GuardianPhone, Status, AvatarHue, Dob, Email)
            VALUES
                (@id, @tenantId, @admissionNo, @name, 'M', 'I', 'A', @classLabel, 1,
                 @guardianName, '9000000099', 'active', 200, '2019-04-23', @email)
            """,
            new
            {
                id, tenantId, admissionNo, name = "Preexisting " + admissionNo,
                classLabel = ClassLabel, guardianName = "Parent " + admissionNo,
                email = admissionNo.ToLowerInvariant() + "@example.com",
            });

        if (optedIn)
        {
            await conn.ExecuteAsync(
                """
                INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, RouteId, FeeHeadId)
                VALUES (@id, @tenantId, @studentId, @routeId, @feeHeadId)
                """,
                new { id = Guid.NewGuid(), tenantId, studentId = id, routeId, feeHeadId });
        }

        return id;
    }

    private static async Task<Guid> SeedRouteAsync(string cs, Guid tenantId, string name)
    {
        var id = Guid.NewGuid();
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name });
        return id;
    }

    [Fact]
    public async Task Reconcile_brings_500_preexisting_students_up_to_date_without_touching_existing_invoices_or_duplicating_on_retry()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        // Published structure + two already-established periods (Ankit's real situation) — one
        // seed student, two admin-run Generate Invoices calls, exactly the pre-existing flow.
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route Reconcile");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client,
            $"{{\"{ClassLabel}\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");
        var ankitId = await CreateSeedStudentAsync(client, routeId, transportHeadId);
        (await GenerateInvoicesAsync(client, "Term 1")).Should().Be(1);
        (await GenerateInvoicesAsync(client, "Annual")).Should().Be(1);

        var ankitInvoicesBefore = await ListInvoicesAsync(fx.ConnectionString, tenantId, [ankitId]);
        ankitInvoicesBefore.Should().HaveCount(2);
        var ankitTerm1IdBefore = ankitInvoicesBefore.Single(i => i.Period == $"{AcademicYear} Term 1").Id;

        // 500 pre-existing students (Aarav's real situation) — inserted with no application
        // code path involved, so no fee hook has ever run for them. Half opted into transport.
        var oldStudentIds = new List<Guid>();
        for (var i = 0; i < OldStudentCount; i++)
        {
            var optedIn = i % 2 == 0;
            var id = await InsertPreExistingStudentAsync(
                fx.ConnectionString, tenantId, $"OLD-{i}", optedIn,
                optedIn ? routeId : null, optedIn ? transportHeadId : null);
            oldStudentIds.Add(id);
        }

        var beforeReconcile = await ListInvoicesAsync(fx.ConnectionString, tenantId, oldStudentIds);
        beforeReconcile.Should().BeEmpty(because: "no fee hook has ever run for these students yet");

        // 1) Run the reconciliation for I-A / 2026-27 (implicit — periods come from what exists).
        var created = await ReconcileAsync(client, ClassLabel);
        created.Should().Be(OldStudentCount * 2, because: "each of the 500 should get both existing periods");

        // 2) Aarav-equivalents get the same fees as everyone else in I-A.
        var afterReconcile = await ListInvoicesAsync(fx.ConnectionString, tenantId, oldStudentIds);
        afterReconcile.Should().HaveCount(OldStudentCount * 2);
        var byStudent = afterReconcile.GroupBy(i => i.StudentId).ToDictionary(g => g.Key, g => g.ToList());
        for (var i = 0; i < oldStudentIds.Count; i++)
        {
            byStudent[oldStudentIds[i]].Should().HaveCount(2);
            var optedIn = i % 2 == 0;
            var expected = optedIn ? 1500 : 1000;
            byStudent[oldStudentIds[i]].Should().OnlyContain(inv => inv.Amount == expected,
                because: $"student {i} (transport opted in: {optedIn}) should be billed {expected}");
        }

        // 3) Ankit's (the already-invoiced seed student's) invoices are completely untouched.
        var ankitInvoicesAfter = await ListInvoicesAsync(fx.ConnectionString, tenantId, [ankitId]);
        ankitInvoicesAfter.Should().HaveCount(2);
        ankitInvoicesAfter.Single(i => i.Period == $"{AcademicYear} Term 1").Id.Should().Be(ankitTerm1IdBefore);

        // 4) Repeated reconciliation creates no duplicates.
        var secondRun = await ReconcileAsync(client, ClassLabel);
        secondRun.Should().Be(0, because: "every eligible invoice was already created by the first run");
        var afterSecondRun = await ListInvoicesAsync(fx.ConnectionString, tenantId, oldStudentIds);
        afterSecondRun.Should().HaveCount(OldStudentCount * 2, because: "retrying reconciliation must not duplicate anything");
    }
}
