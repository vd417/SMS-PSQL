using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;

namespace Sms.Tests.Integration.Finance;

/// Covers M0193_FeeStructure_History: every Save creates a new, immutable FeeStructures row
/// instead of overwriting the previous one, GET /fees/structure keeps resolving "the current
/// one" to edit as the most recent row, and GET /fees/structures lists every saved version.
[Collection("sql")]
public class FeeStructureHistoryTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

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

    private static async Task<JsonElement> SaveStructureAsync(
        HttpClient client, string name, string academicYear, string status = "active",
        string amountsJson = """{"X-A":{"tuition":1000}}""", Guid? id = null) =>
        await Data(await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            id,
            name,
            academic_year = academicYear,
            currency = "INR",
            effective_from = "2025-04-01",
            status,
            amounts_json = amountsJson,
        }), HttpStatusCode.OK);

    private static async Task<Guid> CreateStudentAsync(HttpClient client, string admissionNo, string grade, string section, int roll) =>
        (await Data(await client.PostAsJsonAsync("/v1/students", new
        {
            admission_no = admissionNo,
            name = "History Test Kid " + admissionNo,
            grade,
            section,
            roll,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> HistoryEntryAsync(HttpClient client, Guid id)
    {
        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        return history.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Saving_twice_creates_two_history_entries_instead_of_overwriting()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var first = await SaveStructureAsync(client, "AY fees v1", "2025-26");
        var second = await SaveStructureAsync(client, "AY fees v2", "2025-26");

        first.GetProperty("id").GetGuid().Should().NotBe(second.GetProperty("id").GetGuid(),
            "each Save must create a new row, not update the previous one");

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Get_structure_still_resolves_to_the_most_recently_saved_version()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        await SaveStructureAsync(client, "Older version", "2025-26");
        var latest = await SaveStructureAsync(client, "Newest version", "2025-26");

        var current = await Data(await client.GetAsync("/v1/fees/structure"), HttpStatusCode.OK);
        current.GetProperty("id").GetGuid().Should().Be(latest.GetProperty("id").GetGuid());
        current.GetProperty("name").GetString().Should().Be("Newest version");
    }

    [Fact]
    public async Task History_list_returns_every_saved_version_newest_first_without_amounts()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-1", "X", "A", 1);

        await SaveStructureAsync(client, "Version A", "2024-25");
        await SaveStructureAsync(client, "Version B", "2025-26");
        await SaveStructureAsync(client, "Version C", "2026-27");

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        var names = history.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        names.Should().Equal(["Version C", "Version B", "Version A"], "newest saved version first");

        foreach (var entry in history.EnumerateArray())
        {
            entry.TryGetProperty("amounts", out _).Should().BeFalse("the history list must stay light — no per-class breakdown");
            entry.TryGetProperty("created_at", out var createdAt).Should().BeTrue();
            createdAt.ValueKind.Should().NotBe(JsonValueKind.Null);
            entry.GetProperty("total_amount").GetDecimal().Should().Be(1000, "1 enrolled student × the 1000 rate for X-A");
        }
    }

    [Fact]
    public async Task History_list_shows_projected_revenue_rate_times_enrolled_students_per_class()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-2A", "X", "A", 1);
        await CreateStudentAsync(client, "ADM-HIST-2B", "X", "A", 2);
        await CreateStudentAsync(client, "ADM-HIST-2C", "X", "B", 1);

        var saved = await SaveStructureAsync(client, "Multi-head structure", "2025-26",
            amountsJson: """{"X-A":{"tuition":1000,"transport":500},"X-B":{"tuition":1000}}""");

        // X-A rate 1500 × 2 students = 3000; X-B rate 1000 × 1 student = 1000; total 4000.
        // tuition: (1000×2) + (1000×1) = 3000; transport: 500×2 = 1000.
        var entry = await HistoryEntryAsync(client, saved.GetProperty("id").GetGuid());
        entry.GetProperty("total_amount").GetDecimal().Should().Be(4000);

        var headAmounts = entry.GetProperty("head_amounts").EnumerateArray().ToList();
        headAmounts.Should().HaveCount(2);
        headAmounts.Single(h => h.GetProperty("head_name").GetString() == "tuition")
            .GetProperty("amount").GetDecimal().Should().Be(3000);
        headAmounts.Single(h => h.GetProperty("head_name").GetString() == "transport")
            .GetProperty("amount").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task History_list_shows_zero_revenue_for_a_class_with_no_enrolled_students()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        // No students seeded at all.

        var saved = await SaveStructureAsync(client, "Unenrolled class structure", "2025-26",
            amountsJson: """{"X-A":{"tuition":1000}}""");

        var entry = await HistoryEntryAsync(client, saved.GetProperty("id").GetGuid());
        entry.GetProperty("total_amount").GetDecimal().Should().Be(0, "no students are enrolled in X-A, so projected revenue is 0");
    }

    [Fact]
    public async Task A_past_version_can_be_fetched_by_id_with_its_full_amounts()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var older = await SaveStructureAsync(client, "Old version", "2025-26");
        await SaveStructureAsync(client, "New version", "2025-26");

        var oldId = older.GetProperty("id").GetGuid();
        var fetched = await Data(await client.GetAsync($"/v1/fees/structures/{oldId}"), HttpStatusCode.OK);
        fetched.GetProperty("id").GetGuid().Should().Be(oldId);
        fetched.GetProperty("name").GetString().Should().Be("Old version");
        fetched.GetProperty("amounts").GetProperty("X-A").GetProperty("tuition").GetDecimal().Should().Be(1000);
    }

    [Fact]
    public async Task Saving_a_second_version_as_active_leaves_the_first_one_published()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var first = await SaveStructureAsync(client, "First live version", "2025-26", "active");
        var second = await SaveStructureAsync(client, "Second live version", "2025-26", "active");

        var firstEntry = await HistoryEntryAsync(client, first.GetProperty("id").GetGuid());
        var secondEntry = await HistoryEntryAsync(client, second.GetProperty("id").GetGuid());
        firstEntry.GetProperty("status").GetString().Should().Be("active", "there is no 'only one Published row' rule");
        secondEntry.GetProperty("status").GetString().Should().Be("active");
    }

    [Fact]
    public async Task Publishing_a_draft_leaves_every_other_published_version_unchanged()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var published = await SaveStructureAsync(client, "Published version", "2025-26", "active");
        var draft = await SaveStructureAsync(client, "Draft version", "2025-26", "inactive");
        var draftId = draft.GetProperty("id").GetGuid();

        await Data(await client.PostAsync($"/v1/fees/structures/{draftId}/publish", new StringContent("")), HttpStatusCode.OK);

        var draftEntry = await HistoryEntryAsync(client, draftId);
        draftEntry.GetProperty("status").GetString().Should().Be("active", "the draft itself is now published");

        var publishedEntry = await HistoryEntryAsync(client, published.GetProperty("id").GetGuid());
        publishedEntry.GetProperty("status").GetString().Should().Be("active", "publishing another version must not unpublish this one");
    }

    [Fact]
    public async Task Multiple_fee_structures_can_be_published_at_the_same_time()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var transport = await SaveStructureAsync(client, "Transport", "2025-26", "active",
            amountsJson: """{"X-A":{"transport-head":19000}}""");
        var exam = await SaveStructureAsync(client, "Exam", "2025-26", "inactive",
            amountsJson: """{"X-A":{"exam-head":6500}}""");
        var examId = exam.GetProperty("id").GetGuid();

        await Data(await client.PostAsync($"/v1/fees/structures/{examId}/publish", new StringContent("")), HttpStatusCode.OK);

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        var activeStatuses = history.EnumerateArray()
            .Where(e => e.GetProperty("id").GetGuid() == transport.GetProperty("id").GetGuid() || e.GetProperty("id").GetGuid() == examId)
            .Select(e => e.GetProperty("status").GetString())
            .ToList();
        activeStatuses.Should().AllBe("active", "Transport and Exam are both published at the same time");
    }

    [Fact]
    public async Task Explicit_unpublish_retires_only_that_version()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var a = await SaveStructureAsync(client, "Fee A", "2025-26", "active");
        var b = await SaveStructureAsync(client, "Fee B", "2025-26", "active");
        var aId = a.GetProperty("id").GetGuid();
        var bId = b.GetProperty("id").GetGuid();

        await Data(await client.PostAsync($"/v1/fees/structures/{aId}/unpublish", new StringContent("")), HttpStatusCode.OK);

        var aEntry = await HistoryEntryAsync(client, aId);
        var bEntry = await HistoryEntryAsync(client, bId);
        aEntry.GetProperty("status").GetString().Should().Be("inactive", "explicitly unpublishing A must retire only A");
        bEntry.GetProperty("status").GetString().Should().Be("active", "B was never touched");
    }

    [Fact]
    public async Task Invoice_generation_bills_every_published_structure_without_double_counting_a_repeated_head()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-6", "X", "A", 1);

        await SaveStructureAsync(client, "Transport", "2025-26", "active",
            amountsJson: """{"X-A":{"transport":19000}}""");
        await SaveStructureAsync(client, "Exam", "2025-26", "active",
            amountsJson: """{"X-A":{"exam":6500}}""");
        // A third, more-recently-published structure that repeats "transport" at a different
        // rate must win that one head, not add to it (no double counting).
        await SaveStructureAsync(client, "Transport revised", "2025-26", "active",
            amountsJson: """{"X-A":{"transport":21000}}""");

        var generated = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = "2025-26", term = "Term 1", classes = new[] { "X-A" },
        }), HttpStatusCode.OK);
        generated.GetProperty("created").GetInt32().Should().Be(1);

        var invoices = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        var invoice = invoices.EnumerateArray().Single();
        invoice.GetProperty("amount").GetDecimal().Should().Be(27500, "21,000 (revised transport, not 19,000 + 21,000) + 6,500 exam");
    }

    [Fact]
    public async Task A_draft_version_can_be_deleted()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var draft = await SaveStructureAsync(client, "Throwaway draft", "2025-26", "inactive");
        var draftId = draft.GetProperty("id").GetGuid();

        var res = await client.DeleteAsync($"/v1/fees/structures/{draftId}");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.EnumerateArray().Any(e => e.GetProperty("id").GetGuid() == draftId).Should().BeFalse();
    }

    [Fact]
    public async Task The_currently_published_version_cannot_be_deleted()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var published = await SaveStructureAsync(client, "Live version", "2025-26", "active");
        var publishedId = published.GetProperty("id").GetGuid();

        var res = await client.DeleteAsync($"/v1/fees/structures/{publishedId}");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.EnumerateArray().Any(e => e.GetProperty("id").GetGuid() == publishedId).Should().BeTrue("a refused delete must not remove the row");
    }

    [Fact]
    public async Task Editing_a_draft_and_saving_updates_it_in_place_instead_of_creating_another_one()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-3", "X", "A", 1);

        var draft = await SaveStructureAsync(client, "Original name", "2025-26", "inactive");
        var draftId = draft.GetProperty("id").GetGuid();

        var edited = await SaveStructureAsync(
            client, "Edited name", "2025-26", "inactive",
            amountsJson: """{"X-A":{"tuition":2000}}""", id: draftId);

        edited.GetProperty("id").GetGuid().Should().Be(draftId, "editing a draft must update the same row, not create a new one");

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(1, "editing an unpublished draft must not leave a second row behind");
        var entry = history.EnumerateArray().Single();
        entry.GetProperty("name").GetString().Should().Be("Edited name");
        entry.GetProperty("total_amount").GetDecimal().Should().Be(2000, "1 enrolled student × the edited 2000 rate for X-A");
    }

    [Fact]
    public async Task Editing_a_draft_and_publishing_updates_it_in_place_and_leaves_the_other_live_version_published()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var published = await SaveStructureAsync(client, "Old live version", "2025-26", "active");
        var draft = await SaveStructureAsync(client, "Draft to publish", "2025-26", "inactive");
        var draftId = draft.GetProperty("id").GetGuid();

        var republished = await SaveStructureAsync(client, "Draft to publish", "2025-26", "active", id: draftId);
        republished.GetProperty("id").GetGuid().Should().Be(draftId, "publishing a draft in-edit must update that same row, not create another one");

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(2, "no extra row should appear");

        var publishedEntry = history.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == draftId);
        publishedEntry.GetProperty("status").GetString().Should().Be("active");
        var otherEntry = history.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == published.GetProperty("id").GetGuid());
        otherEntry.GetProperty("status").GetString().Should().Be("active", "publishing this draft must not unpublish the other live version");
    }

    [Fact]
    public async Task Saving_with_the_published_version_s_id_still_creates_a_new_version_instead_of_mutating_it()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);

        var published = await SaveStructureAsync(client, "Live version", "2025-26", "active");
        var publishedId = published.GetProperty("id").GetGuid();

        // Simulates a plain "Save only" from the default (non-editing) view, which still
        // happens to carry the currently-loaded (live) structure's id.
        var saved = await SaveStructureAsync(client, "New draft", "2025-26", "inactive", id: publishedId);

        saved.GetProperty("id").GetGuid().Should().NotBe(publishedId, "the live version must never be mutated in place");

        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        history.GetArrayLength().Should().Be(2);
        var liveEntry = history.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == publishedId);
        liveEntry.GetProperty("name").GetString().Should().Be("Live version", "the published row must be untouched");
        liveEntry.GetProperty("status").GetString().Should().Be("active");
    }

    [Fact]
    public async Task History_list_s_head_breakdown_resolves_a_real_fee_head_id_to_its_current_name()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-4", "X", "A", 1);

        var head = await Data(await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Exam Fee" }), HttpStatusCode.Created);
        var headId = head.GetProperty("id").GetGuid();

        var saved = await SaveStructureAsync(client, "Named head structure", "2025-26",
            amountsJson: $"{{\"X-A\":{{\"{headId}\":8000}}}}");

        var entry = await HistoryEntryAsync(client, saved.GetProperty("id").GetGuid());
        var headAmounts = entry.GetProperty("head_amounts").EnumerateArray().ToList();
        headAmounts.Should().HaveCount(1);
        headAmounts[0].GetProperty("head_name").GetString().Should().Be("Exam Fee");
        headAmounts[0].GetProperty("head_id").GetGuid().Should().Be(headId);
        headAmounts[0].GetProperty("amount").GetDecimal().Should().Be(8000);
    }

    [Fact]
    public async Task History_list_s_head_breakdown_shows_a_readable_label_for_a_head_that_was_since_deleted()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-5", "X", "A", 1);

        var head = await Data(await client.PostAsJsonAsync("/v1/fees/heads", new { name = "Tour Fee" }), HttpStatusCode.Created);
        var headId = head.GetProperty("id").GetGuid();

        var saved = await SaveStructureAsync(client, "Structure with a soon-deleted head", "2025-26",
            amountsJson: $"{{\"X-A\":{{\"{headId}\":6500}}}}");

        (await client.DeleteAsync($"/v1/fees/heads/{headId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var entry = await HistoryEntryAsync(client, saved.GetProperty("id").GetGuid());
        var headAmounts = entry.GetProperty("head_amounts").EnumerateArray().ToList();
        headAmounts.Should().HaveCount(1);
        headAmounts[0].GetProperty("head_name").GetString().Should().NotBe(headId.ToString(),
            "a deleted head must not surface its raw GUID as the display name");
        headAmounts[0].GetProperty("head_name").GetString().Should().Contain("Deleted fee head");
        headAmounts[0].GetProperty("amount").GetDecimal().Should().Be(6500);
    }

    /// End-to-end acceptance test: fee publishing is completely independent of Term. Publish
    /// takes no Term, stores no TermId, and any number of versions stay Published at once.
    /// Term is chosen only when generating invoices, and it determines which currently-
    /// Published fees get billed — never the other way around. Already-generated invoices for
    /// an earlier Term are never touched by a fee published afterwards.
    [Fact]
    public async Task Publishing_fees_is_independent_of_term_and_billing_picks_up_whatever_is_published_at_generation_time()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var client = PrincipalClient(app, tenantId);
        await CreateStudentAsync(client, "ADM-HIST-6", "X", "A", 1);

        // 1-3: create and publish 4 independent fee structures — no Term involved anywhere.
        var feeIds = new List<Guid>();
        foreach (var (name, rate) in new[] { ("Fee A", 100m), ("Fee B", 200m), ("Fee C", 300m), ("Fee D", 400m) })
        {
            var draft = await SaveStructureAsync(client, name, "2025-26", "inactive", amountsJson: $"{{\"X-A\":{{\"{name}\":{rate}}}}}");
            var id = draft.GetProperty("id").GetGuid();
            var publishRes = await client.PostAsync($"/v1/fees/structures/{id}/publish", new StringContent(""));
            publishRes.StatusCode.Should().Be(HttpStatusCode.OK);
            feeIds.Add(id);
        }

        // 3: all 4 show Published, and publishing D did not change A/B/C (or vice versa).
        var history = await Data(await client.GetAsync("/v1/fees/structures"), HttpStatusCode.OK);
        foreach (var id in feeIds)
        {
            history.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id)
                .GetProperty("status").GetString().Should().Be("active", "every one of the 4 fees must be Published simultaneously");
        }

        // 6-7: select Term 1 and generate — it must pick up all 4 currently-Published fees.
        var gen1 = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate",
            new { academic_year = "2025-26", term = "Term 1", classes = new[] { "X-A" } }), HttpStatusCode.OK);
        gen1.GetProperty("created").GetInt32().Should().Be(1);

        var student = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        var term1Invoice = student.EnumerateArray().Single(i => i.GetProperty("period").GetString() == "2025-26 Term 1");
        term1Invoice.GetProperty("amount").GetDecimal().Should().Be(1000, "100+200+300+400 — every currently-Published fee");

        // 8-9: publish a 5th fee AFTER Term 1 was generated — Term 1 must stay exactly as it was.
        var lateDraft = await SaveStructureAsync(client, "Fee E (late)", "2025-26", "inactive", amountsJson: """{"X-A":{"Fee E":500}}""");
        var lateId = lateDraft.GetProperty("id").GetGuid();
        (await client.PostAsync($"/v1/fees/structures/{lateId}/publish", new StringContent(""))).StatusCode.Should().Be(HttpStatusCode.OK);

        var afterLatePublish = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        var term1Again = afterLatePublish.EnumerateArray().Single(i => i.GetProperty("period").GetString() == "2025-26 Term 1");
        term1Again.GetProperty("amount").GetDecimal().Should().Be(1000, "Fee E must NOT be inserted into the already-generated Term 1 invoice");

        // 10: the next unbilled Term picks up Fee E (and everything else still Published).
        var gen2 = await Data(await client.PostAsJsonAsync("/v1/fees/invoices/generate",
            new { academic_year = "2025-26", term = "Term 2", classes = new[] { "X-A" } }), HttpStatusCode.OK);
        gen2.GetProperty("created").GetInt32().Should().Be(1);

        var afterTerm2 = await Data(await client.GetAsync("/v1/fees/invoices"), HttpStatusCode.OK);
        var term2Invoice = afterTerm2.EnumerateArray().Single(i => i.GetProperty("period").GetString() == "2025-26 Term 2");
        term2Invoice.GetProperty("amount").GetDecimal().Should().Be(1500, "100+200+300+400+500 — Fee E is now included too");
    }
}
