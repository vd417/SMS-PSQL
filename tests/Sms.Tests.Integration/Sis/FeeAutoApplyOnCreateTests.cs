using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Sis;

/// Covers the exact real-world scenario that motivated FeeService.ApplyExistingFeeStructureAsync:
/// a class already has a published Fee Structure AND already-generated invoice periods (from an
/// admin's earlier Generate Invoices run), then 500 new students are bulk-imported into that same
/// class — they must receive the existing applicable fees automatically, with no separate
/// bulk-import fee logic, no invented periods, correct transport gating, and no duplicates.
[Collection("sql")]
public class FeeAutoApplyOnCreateTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string AcademicYear = "2026-27";
    private const string ClassLabel = "I-A";
    private const int NewStudentCount = 500;

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

    private sealed record InvoiceRow(Guid Id, Guid StudentId, string Period, decimal Amount);

    /// One query for every student's invoices instead of one HTTP round trip per student — the
    /// app's own rate limiter (rightly) refuses 500 GETs in a burst, and this is also just the
    /// correct way to assert bulk state: read ground truth directly.
    private static async Task<IReadOnlyList<InvoiceRow>> ListInvoicesAsync(
        string cs, Guid tenantId, IReadOnlyList<Guid> studentIds)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        var rows = await conn.QueryAsync<InvoiceRow>(
            "SELECT Id, StudentId, Period, Amount FROM dbo.FeeInvoices WHERE StudentId IN @ids",
            new { ids = studentIds });
        return rows.ToList();
    }

    private static async Task<Guid> SeedRouteAsync(string cs, Guid tenantId, string name)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name });
        return id;
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

    private static async Task<Guid> CreateSeedStudentAsync(
        HttpClient client, Guid routeId, Guid transportHeadId)
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

    private static object BulkRow(int rowNumber, string admissionNo, bool transportOptedIn, Guid? routeId, Guid? feeHeadId) => new
    {
        row_number = rowNumber,
        create_student_request = new
        {
            admission_no = admissionNo,
            name = "Bulk Kid " + admissionNo,
            grade = "I",
            section = "A",
            roll = rowNumber,
            guardian_name = "Parent " + admissionNo,
            guardian_phone = "9" + rowNumber.ToString().PadLeft(9, '0'),
            email = admissionNo.ToLowerInvariant() + "@example.com",
            gender = "M",
            dob = "2019-04-23",
        },
        extras_json = "{}",
        transport = transportOptedIn
            ? new { opted_in = true, route_id = routeId, fee_head_id = feeHeadId }
            : null,
    };

    private static async Task<JsonElement> RunBulkBatchAsync(
        HttpClient client, Guid importId, int batchIndex, Guid routeId, Guid transportHeadId, int startIndex, int count)
    {
        var rows = Enumerable.Range(startIndex, count)
            .Select(i => BulkRow(
                rowNumber: i + 2,
                admissionNo: $"ADM-500-{i}",
                transportOptedIn: i % 2 == 0, // half opted in, half not — exercises both branches
                routeId: i % 2 == 0 ? routeId : null,
                feeHeadId: i % 2 == 0 ? transportHeadId : null))
            .ToArray();

        return await Data(await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = importId,
            batch_index = batchIndex,
            rows,
        }), HttpStatusCode.OK);
    }

    /// The endpoint caps a single batch at 200 rows, so a real 500-student import is chunked
    /// client-side, same as the app's own bulk-import UI would do — one call per <=200-row page,
    /// sharing one importId across all of a run's batches (each with its own batchIndex).
    private static async Task<(int Created, List<Guid> StudentIds)> RunAllBatchesAsync(
        HttpClient client, Guid importId, Guid routeId, Guid transportHeadId, int totalCount)
    {
        var created = 0;
        var ids = new List<Guid>();
        for (int start = 0, batchIndex = 0; start < totalCount; start += 200, batchIndex++)
        {
            var take = Math.Min(200, totalCount - start);
            var batch = await RunBulkBatchAsync(client, importId, batchIndex, routeId, transportHeadId, start, take);
            created += batch.GetProperty("created").GetInt32();
            ids.AddRange(batch.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("student_id").GetGuid()));
        }
        return (created, ids);
    }

    [Fact]
    public async Task Importing_500_students_after_a_published_structure_and_established_periods_auto_applies_existing_fees()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = PrincipalClient(app, tenantId);

        // 1) Published fee structure for I-A: tuition (non-transport) + a transport-flagged head.
        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Route 500");
        var transportHeadId = await CreateTransportHeadAsync(client, "Transport");
        await UpsertStructureAsync(client,
            $"{{\"{ClassLabel}\":{{\"tuition\":1000,\"{transportHeadId}\":500}}}}");

        // 2) Establish two real periods the normal way: one seed student, opted into transport,
        // then an admin-run Generate Invoices for each period — exactly today's existing flow.
        var seedStudentId = await CreateSeedStudentAsync(client, routeId, transportHeadId);
        (await GenerateInvoicesAsync(client, "Term 1")).Should().Be(1);
        (await GenerateInvoicesAsync(client, "Annual")).Should().Be(1);

        var seedInvoicesBefore = await ListInvoicesAsync(fx.ConnectionString, tenantId, [seedStudentId]);
        seedInvoicesBefore.Should().HaveCount(2);
        var seedTerm1IdBefore = seedInvoicesBefore.Single(i => i.Period == $"{AcademicYear} Term 1").Id;

        // 3) Bulk import 500 NEW students into the same class, after those periods already exist
        // (chunked into <=200-row batches — see RunAllBatchesAsync).
        var importId = Guid.NewGuid();
        var (createdCount, studentIds) = await RunAllBatchesAsync(client, importId, routeId, transportHeadId, NewStudentCount);
        createdCount.Should().Be(NewStudentCount);

        // 4) Every one of the 500 must now have BOTH periods, automatically, with no manual
        // Generate Invoices re-run — this is the actual requirement. One query, not 500.
        var newInvoices = await ListInvoicesAsync(fx.ConnectionString, tenantId, studentIds);
        newInvoices.Should().HaveCount(NewStudentCount * 2, because: "every one of the 500 should get both existing periods");
        var byStudent = newInvoices.GroupBy(i => i.StudentId).ToDictionary(g => g.Key, g => g.ToList());
        for (var i = 0; i < studentIds.Count; i++)
        {
            byStudent.TryGetValue(studentIds[i], out var invoicesForStudent).Should().BeTrue(because: $"student index {i} should have invoices");
            invoicesForStudent!.Should().HaveCount(2, because: $"student index {i} should have both periods");

            var optedIn = i % 2 == 0;
            var expected = optedIn ? 1500 : 1000;
            invoicesForStudent!.Should().OnlyContain(inv => inv.Amount == expected,
                because: $"student index {i} (transport opted in: {optedIn}) should be billed {expected}");
        }

        // 5) Retry the identical batches (same importId/batchIndex per chunk) — must not
        // duplicate anything.
        var (retryCreatedCount, _) = await RunAllBatchesAsync(client, importId, routeId, transportHeadId, NewStudentCount);
        retryCreatedCount.Should().Be(NewStudentCount, because: "the cached batch result is replayed, not reprocessed");
        var invoicesAfterRetry = await ListInvoicesAsync(fx.ConnectionString, tenantId, studentIds);
        invoicesAfterRetry.Should().HaveCount(NewStudentCount * 2, because: "retrying the batch must not create duplicate invoices");

        // 6) The seed student's original invoice is untouched.
        var seedInvoicesAfter = await ListInvoicesAsync(fx.ConnectionString, tenantId, [seedStudentId]);
        seedInvoicesAfter.Should().HaveCount(2);
        seedInvoicesAfter.Single(i => i.Period == $"{AcademicYear} Term 1").Id.Should().Be(seedTerm1IdBefore);
    }
}
