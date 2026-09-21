using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Sis;

[Collection("sql")]
public class StudentBulkImportServiceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient TenantClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.owner"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static object Row(int n, string name, string phone, string email, string? admissionNo = null) => new
    {
        row_number = n,
        create_student_request = new
        {
            admission_no = admissionNo, name, gender = "M", grade = "I", section = "A", roll = 0,
            guardian_name = name, guardian_phone = phone, guardian_email = email,
            house = (string?)null, avatar_hue = 0, dob = "2015-01-01", email, address = (string?)null,
        },
        extras_json = "{}",
        transport = (object?)null,
    };

    private static object RowWithTransport(int n, string name, string phone, string email, Guid routeId) => new
    {
        row_number = n,
        create_student_request = new
        {
            admission_no = (string?)null, name, gender = "M", grade = "I", section = "A", roll = 0,
            guardian_name = name, guardian_phone = phone, guardian_email = email,
            house = (string?)null, avatar_hue = 0, dob = "2015-01-01", email, address = (string?)null,
        },
        extras_json = "{}",
        transport = new { opted_in = true, route_id = routeId, stop_id = (Guid?)null, fee_head_id = (Guid?)null },
    };

    private static async Task Seed(string cs, Guid tenantId, Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@t", new { t = tenantId });
        await work(conn);
    }

    private static async Task<Guid> SeedRouteAsync(string cs, Guid tenantId, string name)
    {
        var id = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.TransportRoutes (Id, TenantId, Name) VALUES (@Id, @TenantId, @Name)",
            new { Id = id, TenantId = tenantId, Name = name }));
        return id;
    }

    private static async Task<Guid> SeedBusOnRouteAsync(string cs, Guid tenantId, Guid routeId, string busNo, int? capacity)
    {
        var id = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Buses (Id, TenantId, BusNo, RouteId, Capacity) VALUES (@Id, @TenantId, @BusNo, @RouteId, @Capacity)",
            new { Id = id, TenantId = tenantId, BusNo = busNo, RouteId = routeId, Capacity = capacity }));
        return id;
    }

    private static async Task<Guid> SeedStudentSeatAsync(string cs, Guid tenantId, Guid busId)
    {
        var studentId = Guid.NewGuid();
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name) VALUES (@Id, @TenantId, @Adm, 'Seat Filler')",
            new { Id = studentId, TenantId = tenantId, Adm = $"SEAT-{studentId:N}" }));
        await Seed(cs, tenantId, conn => conn.ExecuteAsync(
            "INSERT dbo.StudentBusAssignments (Id, TenantId, StudentId, BusId) VALUES (@Id, @TenantId, @S, @B)",
            new { Id = Guid.NewGuid(), TenantId = tenantId, S = studentId, B = busId }));
        return studentId;
    }

    [Fact]
    public async Task ProcessBatch_creates_all_valid_rows_and_returns_real_counts()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var importId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = importId,
            batch_index = 0,
            rows = new[]
            {
                Row(1, "Aarav Sharma", "9876543210", "aarav@example.com"),
                Row(2, "Aditi Verma", "9876543211", "aditi@example.com"),
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        var data = body!.Data;
        data.processed.Should().Be(2);
        data.created.Should().Be(2);
        data.skipped.Should().Be(0);
        data.rows.Should().HaveCount(2);
        data.rows[0].status.Should().Be("created");
        data.rows[0].student_id.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessBatch_skips_a_row_missing_a_required_field_without_failing_the_batch()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var badRow = Row(1, "", "9876543212", "blank-name@example.com"); // blank name -> should be skipped
        var goodRow = Row(2, "Rahul Gupta", "9876543213", "rahul@example.com");

        var resp = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = Guid.NewGuid(),
            batch_index = 0,
            rows = new[] { badRow, goodRow },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        var data = body!.Data;
        data.created.Should().Be(1);
        data.skipped.Should().Be(1);
        data.rows.First(r => r.row_number == 1).status.Should().Be("skipped");
        data.rows.First(r => r.row_number == 1).error.Should().NotBeNullOrEmpty();
        data.rows.First(r => r.row_number == 2).status.Should().Be("created");
    }

    [Fact]
    public async Task ProcessBatch_replaying_the_same_import_and_batch_index_never_creates_duplicates()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = TenantClient(app, tenantId);
        var importId = Guid.NewGuid();
        var payload = new
        {
            import_id = importId,
            batch_index = 0,
            rows = new[] { Row(1, "Neha Singh", "9876543214", "neha@example.com") },
        };

        var first = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", payload);
        var second = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", payload);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        var secondBody = await second.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        secondBody!.Data.rows[0].student_id.Should().Be(firstBody!.Data.rows[0].student_id);

        // Confirm no duplicate row was actually inserted: list this tenant's grade "I" students
        // (the /v1/students `q` search only matches name/admission-no/class-label, not email,
        // so filter client-side by email on the tenant-scoped list rather than via `q=`).
        var check = await client.GetAsync("/v1/students?grade=I");
        var students = await check.Content.ReadFromJsonAsync<StudentListEnvelopeDto>();
        students!.data.Count(s => s.email == "neha@example.com").Should().Be(1);
    }

    [Fact]
    public async Task ProcessBatch_opting_into_transport_with_no_bus_capacity_still_creates_and_reports_pending()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var client = TenantClient(app, tenantId);

        var routeId = await SeedRouteAsync(fx.ConnectionString, tenantId, "Bulk Route Full");
        var busId = await SeedBusOnRouteAsync(fx.ConnectionString, tenantId, routeId, "BULK-FULL-1", capacity: 1);
        await SeedStudentSeatAsync(fx.ConnectionString, tenantId, busId); // fills the bus's only seat

        var resp = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = Guid.NewGuid(),
            batch_index = 0,
            rows = new[] { RowWithTransport(1, "Priya Nair", "9876543215", "priya@example.com", routeId) },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        var data = body!.Data;
        data.created.Should().Be(1);
        data.transport_pending.Should().Be(1);
        data.rows[0].status.Should().Be("created");
        data.rows[0].student_id.Should().NotBeNull();
        data.rows[0].transport_status.Should().Be("pending");
    }

    [Fact]
    public async Task ProcessBatch_a_row_that_fails_at_the_database_layer_does_not_abort_other_rows()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // Both rows share the same explicit AdmissionNo: the first insert succeeds, and the
        // second hits the real UX_Students_Tenant_AdmissionNo unique-index violation inside
        // dbo.Student_Create — a genuine DB-layer failure, not the pre-flight guard.
        var duplicateAdmissionNo = $"DUP-{Guid.NewGuid():N}";
        var firstRow = Row(1, "Karan Mehta", "9876543216", "karan@example.com", duplicateAdmissionNo);
        var secondRow = Row(2, "Kiran Shah", "9876543217", "kiran@example.com", duplicateAdmissionNo);

        var resp = await client.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = Guid.NewGuid(),
            batch_index = 0,
            rows = new[] { firstRow, secondRow },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        var data = body!.Data;
        data.created.Should().Be(1);
        data.skipped.Should().Be(1);
        data.rows.First(r => r.row_number == 1).status.Should().Be("created");
        data.rows.First(r => r.row_number == 1).student_id.Should().NotBeNull();
        var failedRow = data.rows.First(r => r.row_number == 2);
        failedRow.status.Should().Be("skipped");
        failedRow.error.Should().NotBeNullOrEmpty();
        // The raw SQL exception message (constraint/table names) must never leak to the caller.
        failedRow.error.Should().NotContain("UX_Students_Tenant_AdmissionNo");
        failedRow.error.Should().NotContain("SqlException");
    }

    [Fact]
    public async Task ProcessBatch_one_row_with_a_null_name_never_rejects_the_whole_batch()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // Raw JSON: send create_student_request.name as null for one row, alongside a valid row.
        // If ASP.NET Core's automatic model-state validation rejects the whole body because of
        // this single null on an otherwise non-nullable property, this whole request comes back
        // as 400 instead of 200 with a per-row partial result.
        const string payload = """
        {
            "import_id": "3e6a35f0-9f39-4d0a-8f2f-5a0e6a2b6a11",
            "batch_index": 0,
            "rows": [
                {
                    "row_number": 1,
                    "create_student_request": {
                        "admission_no": null, "name": null, "gender": "M", "grade": "I", "section": "A",
                        "roll": 0, "guardian_name": "Someone", "guardian_phone": "9876543218",
                        "guardian_email": "someone@example.com", "house": null, "avatar_hue": 0,
                        "dob": "2015-01-01", "email": "null-name@example.com", "address": null
                    },
                    "extras_json": "{}",
                    "transport": null
                },
                {
                    "row_number": 2,
                    "create_student_request": {
                        "admission_no": null, "name": "Valid Row", "gender": "M", "grade": "I", "section": "A",
                        "roll": 0, "guardian_name": "Valid Row", "guardian_phone": "9876543219",
                        "guardian_email": "validrow@example.com", "house": null, "avatar_hue": 0,
                        "dob": "2015-01-01", "email": "validrow@example.com", "address": null
                    },
                    "extras_json": "{}",
                    "transport": null
                }
            ]
        }
        """;

        var resp = await client.PostAsync("/v1/students/bulk-import/batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var responseBody = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"a malformed row must be skipped per-row, not reject the whole batch. Actual body: {responseBody}");

        var body = JsonSerializer.Deserialize<DataEnvelopeDto<BulkImportBatchResponseDto>>(
            responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var data = body!.Data;
        data.created.Should().Be(1);
        data.skipped.Should().Be(1);
        data.rows.First(r => r.row_number == 1).status.Should().Be("skipped");
        data.rows.First(r => r.row_number == 2).status.Should().Be("created");
    }

    [Fact]
    public async Task ProcessBatch_missing_rows_field_returns_a_clean_validation_error_not_a_500()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // `rows` omitted entirely (not merely an empty array) — this is exactly the shape that
        // used to be caught for free by [ApiController]'s automatic model validation before
        // SkipModelValidationAttribute cleared ModelState for this action. Without the explicit
        // guard in ProcessBatchAsync, this previously threw an unhandled NullReferenceException
        // from the `foreach (var row in req.Rows)` loop instead of a clean 400.
        const string payload = """
        {
            "import_id": "6b1a5b4a-1a2b-4c3d-9e0f-1a2b3c4d5e6f",
            "batch_index": 0
        }
        """;

        var resp = await client.PostAsync("/v1/students/bulk-import/batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var responseBody = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"a missing `rows` field must be a clean validation error, not an unhandled exception. Actual body: {responseBody}");
        responseBody.Should().Contain("validation_error");
        responseBody.Should().NotContain("NullReferenceException");
    }

    [Fact]
    public async Task ProcessBatch_null_rows_field_returns_a_clean_validation_error_not_a_500()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        const string payload = """
        {
            "import_id": "6b1a5b4a-1a2b-4c3d-9e0f-1a2b3c4d5e6f",
            "batch_index": 0,
            "rows": null
        }
        """;

        var resp = await client.PostAsync("/v1/students/bulk-import/batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var responseBody = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"an explicit null `rows` field must be a clean validation error, not an unhandled exception. Actual body: {responseBody}");
        responseBody.Should().Contain("validation_error");
        responseBody.Should().NotContain("NullReferenceException");
    }

    [Fact]
    public async Task ProcessBatch_a_non_guid_route_id_returns_400_not_500()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // Real admin CSVs carry route NAMES, not GUIDs. If the client fails to resolve one, the
        // raw name reaches transport.route_id. System.Text.Json cannot bind "Ring Road" to a Guid,
        // so the whole body fails to bind and `req` arrives null — which used to be masked by
        // SkipModelValidationAttribute's blanket ModelState.Clear() and blew up as a 500
        // NullReferenceException for the entire 200-row batch.
        const string payload = """
        {
            "import_id": "0f6b6a1c-2d3e-4f50-8a1b-2c3d4e5f6a7b",
            "batch_index": 0,
            "rows": [
                {
                    "row_number": 1,
                    "create_student_request": {
                        "admission_no": null, "name": "Bad Route Row", "gender": "M", "grade": "I", "section": "A",
                        "roll": 0, "guardian_name": "Bad Route Row", "guardian_phone": "9876543220",
                        "guardian_email": "badroute@example.com", "house": null, "avatar_hue": 0,
                        "dob": "2015-01-01", "email": "badroute@example.com", "address": null
                    },
                    "extras_json": "{}",
                    "transport": { "opted_in": true, "route_id": "Ring Road", "stop_id": null, "fee_head_id": null }
                }
            ]
        }
        """;

        var resp = await client.PostAsync("/v1/students/bulk-import/batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var responseBody = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"a non-GUID route_id must be a clean 400, not a 500 that fails the whole batch. Actual body: {responseBody}");
        responseBody.Should().NotContain("NullReferenceException");
        // Actionable: the error message must name the offending field so an admin can fix the CSV.
        responseBody.ToLowerInvariant().Should().Contain("route_id");
    }

    [Fact]
    public async Task ProcessBatch_a_malformed_import_id_returns_400_not_a_silent_empty_guid()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // A malformed import_id used to be swallowed by the same ModelState.Clear() and silently
        // coerced to Guid.Empty — a shared idempotency key across unrelated imports, which would
        // make one import's recorded batch result replay for a completely different import.
        const string payload = """
        {
            "import_id": "not-a-guid",
            "batch_index": 0,
            "rows": [
                {
                    "row_number": 1,
                    "create_student_request": {
                        "admission_no": null, "name": "Bad Import Id Row", "gender": "M", "grade": "I", "section": "A",
                        "roll": 0, "guardian_name": "Bad Import Id Row", "guardian_phone": "9876543221",
                        "guardian_email": "badimport@example.com", "house": null, "avatar_hue": 0,
                        "dob": "2015-01-01", "email": "badimport@example.com", "address": null
                    },
                    "extras_json": "{}",
                    "transport": null
                }
            ]
        }
        """;

        var resp = await client.PostAsync("/v1/students/bulk-import/batch",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var responseBody = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"a malformed import_id must be rejected, never coerced to Guid.Empty. Actual body: {responseBody}");
        responseBody.Should().NotContain("NullReferenceException");
        responseBody.ToLowerInvariant().Should().Contain("import_id");
    }

    [Fact]
    public async Task ProcessBatch_only_creates_students_in_the_caller_tenant()
    {
        await using var app = App();
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var tenantAClient = TenantClient(app, tenantAId);
        var tenantBClient = TenantClient(app, tenantBId);

        // Ensure both tenants exist in the database
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantAId, tier: "platinum");
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantBId, tier: "platinum");

        var importId = Guid.NewGuid();
        var resp = await tenantAClient.PostAsJsonAsync("/v1/students/bulk-import/batch", new
        {
            import_id = importId,
            batch_index = 0,
            rows = new[] { Row(1, "Tenant A Student", "9876500001", "tenanta@example.com") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Positive control: confirm Tenant A's call actually created the student (not just returned OK).
        // This proves that the later "Tenant B can't see it" assertion is validating isolation,
        // not just absence of data.
        var tenantABody = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        tenantABody!.Data.created.Should().Be(1);

        // Tenant B queries by grade (the only search that works) and verifies the student isn't in their view.
        // See ProcessBatch_replaying_the_same_import_and_batch_index_never_creates_duplicates for the rationale:
        // /v1/students `q` search only matches name/admission-no/class-label, not email, so we filter client-side.
        var check = await tenantBClient.GetAsync("/v1/students?grade=I");
        var students = await check.Content.ReadFromJsonAsync<StudentListEnvelopeDto>();
        students!.data.Should().NotContain(s => s.email == "tenanta@example.com");
    }

    private sealed record DataEnvelopeDto<T>(T Data);
    private sealed record BulkImportRowResponseDto(
        int row_number, string? student_id, string status, string? error, string? transport_status);
    private sealed record BulkImportBatchResponseDto(
        Guid import_id, int batch_index, int processed, int created, int skipped, int transport_pending,
        List<BulkImportRowResponseDto> rows);
    private sealed record StudentListItemDto(string? email);
    private sealed record StudentListEnvelopeDto(List<StudentListItemDto> data, string? next_cursor);
}
