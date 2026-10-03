using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

// B6: bulk student import enforces per-tenant contact uniqueness on the student's OWN email —
// the only own-contact a bulk row can set. A CreateStudentRequest carries `Email` (own) plus the
// denormalized guardian fields `GuardianEmail`/`GuardianPhone`; there is no own-phone column.
//
// Enforcement is NOT a second mechanism bolted onto bulk import — it is the SAME authoritative
// B5 path every row already travels: SisService.CreateStudentAsync -> dbo.Student_Create, which
// PERFORMs dbo.contact_claims_sync on the own Email and raises the SMSDC sentinel on a
// different-person conflict. BaseRepository maps that to ContactConflictException, SisService
// returns the friendly 409 `conflict`, and the per-row bulk-import loop records that one row as
// status:"skipped" (with the friendly message) while every other valid row is still "created".
// Each row create is its own connection/transaction, so a rolled-back conflicting row can never
// poison the batch.
//
// Reachable plan scenarios pinned here: (a) two rows sharing a normalized own email; (b) a row
// whose own email already belongs to a different person (a Teacher) in the school; (d) siblings
// sharing a guardian email -> both created (guardian exempt); the guardian-phone no-op (guardian
// phone is never claimed -> both created); (f) partial import. NOT reachable (documented in the
// task report, not forced into a test): (c) same-PersonId reuse across rows — bulk-imported
// students always carry PersonId = NULL, so no shared-person reuse can be expressed; (e) own
// phone +91-vs-bare — students have no own-phone column, so phone is never claimed for a student.
[Collection("sql")]
public class ContactBulkImportTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";
    private const string BatchUrl = "/v1/students/bulk-import/batch";

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
        // owner for bulk import + admin for the /v1/teachers control row in scenario (b).
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.owner", "school.admin"], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    // A bulk row. `ownEmail` is the student's OWN (claimed) email; `guardianEmail`/`guardianPhone`
    // are the denormalized guardian fields (never claimed). Guardian email defaults to a distinct
    // value so it never accidentally collides with another row's OWN email.
    private static object Row(
        int n, string name, string ownEmail,
        string? guardianEmail = null, string guardianPhone = "9000000000", string? admissionNo = null) => new
    {
        row_number = n,
        create_student_request = new
        {
            admission_no = admissionNo, name, gender = "M", grade = "I", section = "A", roll = 0,
            guardian_name = name, guardian_phone = guardianPhone,
            guardian_email = guardianEmail ?? ("guardian-of-" + ownEmail),
            house = (string?)null, avatar_hue = 0, dob = "2015-01-01", email = ownEmail, address = (string?)null,
        },
        extras_json = "{}",
        transport = (object?)null,
    };

    private static object TeacherBody(string email, string name = "Existing Teacher") => new
    {
        name, gender = "M", department = "Science", designation = "Teacher",
        subjects = Array.Empty<string>(), phone = (string?)null, email,
        exp = 1, rating = 4.0, result = 80, load = 10, avatar_hue = 100, top = false,
    };

    private static async Task<BulkImportBatchResponseDto> PostBatchAsync(
        HttpClient client, Guid importId, params object[] rows)
    {
        var resp = await client.PostAsJsonAsync(BatchUrl, new { import_id = importId, batch_index = 0, rows });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DataEnvelopeDto<BulkImportBatchResponseDto>>();
        return body!.Data;
    }

    // (a) Two rows in one file share a normalized own email -> first created, second skipped with a
    //     row-level conflict; the batch is not aborted.
    [Fact]
    public async Task Two_rows_sharing_the_same_own_email_create_the_first_and_skip_the_second()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        // "Dup@X.com" vs "dup@x.com" — normalized (lower/trim) they are the SAME claim.
        var data = await PostBatchAsync(client, Guid.NewGuid(),
            Row(1, "First", "Dup@X.com", guardianPhone: "9111100001"),
            Row(2, "Second", "dup@x.com", guardianPhone: "9111100002"));

        data.created.Should().Be(1);
        data.skipped.Should().Be(1);
        data.rows.First(r => r.row_number == 1).status.Should().Be("created");
        data.rows.First(r => r.row_number == 1).student_id.Should().NotBeNull();
        var skipped = data.rows.First(r => r.row_number == 2);
        skipped.status.Should().Be("skipped");
        skipped.student_id.Should().BeNull();
        skipped.error.Should().NotBeNullOrEmpty();
        skipped.error!.ToLowerInvariant().Should().Contain("email");
        // The raw sentinel / SQL internals must never leak to the caller.
        skipped.error.Should().NotContain("SMSDC");
        skipped.error.Should().NotContain("contact_conflict");
    }

    // (b) A row whose own email already belongs to a DIFFERENT person (a Teacher) in the same
    //     school is skipped (cross-owner-type enforcement), the rest of the batch still imports.
    [Fact]
    public async Task Row_whose_own_email_belongs_to_an_existing_teacher_is_skipped()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        (await client.PostAsJsonAsync("/v1/teachers", TeacherBody("taken@x.com")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var data = await PostBatchAsync(client, Guid.NewGuid(),
            Row(1, "Clashes With Teacher", "taken@x.com", guardianPhone: "9222200001"),
            Row(2, "Fine", "fine@x.com", guardianPhone: "9222200002"));

        data.created.Should().Be(1);
        data.skipped.Should().Be(1);
        var skipped = data.rows.First(r => r.row_number == 1);
        skipped.status.Should().Be("skipped");
        skipped.error.Should().NotBeNullOrEmpty();
        data.rows.First(r => r.row_number == 2).status.Should().Be("created");
    }

    // (d) Two sibling rows share the SAME guardian email (distinct own emails) -> BOTH created.
    //     Guardian email is a denormalized field and is never claimed.
    [Fact]
    public async Task Two_siblings_sharing_a_guardian_email_are_both_created()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var data = await PostBatchAsync(client, Guid.NewGuid(),
            Row(1, "Sibling One", "sib1@x.com", guardianEmail: "shared.parent@x.com", guardianPhone: "9333300001"),
            Row(2, "Sibling Two", "sib2@x.com", guardianEmail: "shared.parent@x.com", guardianPhone: "9333300002"));

        data.created.Should().Be(2);
        data.skipped.Should().Be(0);
        data.rows.Should().OnlyContain(r => r.status == "created");
    }

    // Guardian-phone no-op (documents the exemption for the field that students have no OWN
    // equivalent of): two sibling rows share the SAME guardian phone (distinct own emails) ->
    // BOTH created. Guardian phone is never claimed, so +91-vs-bare normalization is irrelevant
    // here — the shared guardian phone can never produce a conflict.
    [Fact]
    public async Task Two_siblings_sharing_a_guardian_phone_are_both_created()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var data = await PostBatchAsync(client, Guid.NewGuid(),
            Row(1, "Sib A", "siba@x.com", guardianPhone: "+91 93333 44444"),
            Row(2, "Sib B", "sibb@x.com", guardianPhone: "9333344444"));

        data.created.Should().Be(2);
        data.skipped.Should().Be(0);
        data.rows.Should().OnlyContain(r => r.status == "created");
    }

    // (f) Partial import: exactly one conflicting own-email row is skipped while every other valid
    //     row in the same batch is created; the skipped row carries a row-level Error.
    [Fact]
    public async Task Partial_import_skips_only_the_conflicting_own_email_row()
    {
        await using var app = App();
        var client = TenantClient(app, Guid.NewGuid());

        var data = await PostBatchAsync(client, Guid.NewGuid(),
            Row(1, "Row One", "p1@x.com", guardianPhone: "9444400001"),
            Row(2, "Row Two (dup of 1)", "p1@x.com", guardianPhone: "9444400002"), // conflicts with row 1
            Row(3, "Row Three", "p3@x.com", guardianPhone: "9444400003"),
            Row(4, "Row Four", "p4@x.com", guardianPhone: "9444400004"));

        data.processed.Should().Be(4);
        data.created.Should().Be(3);
        data.skipped.Should().Be(1);

        var skipped = data.rows.First(r => r.row_number == 2);
        skipped.status.Should().Be("skipped");
        skipped.error.Should().NotBeNullOrEmpty();
        skipped.student_id.Should().BeNull();

        data.rows.Where(r => r.row_number != 2).Should().OnlyContain(r => r.status == "created");
    }

    private sealed record DataEnvelopeDto<T>(T Data);
    private sealed record BulkImportRowResponseDto(
        int row_number, string? student_id, string status, string? error, string? transport_status);
    private sealed record BulkImportBatchResponseDto(
        Guid import_id, int batch_index, int processed, int created, int skipped, int transport_pending,
        List<BulkImportRowResponseDto> rows);
}
