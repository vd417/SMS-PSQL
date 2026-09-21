using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// <summary>
/// A scripted <see cref="IAiClassificationClient"/> whose result can be swapped BETWEEN requests
/// against the same <see cref="WebApplicationFactory{TEntryPoint}"/> instance -- registered as a
/// singleton (unlike <see cref="ScriptedClassificationClient"/>'s per-instance closure in
/// AiSearchSecurityTests.cs) specifically so a multi-turn conversation test can mutate
/// <see cref="Result"/> before each subsequent HTTP call while reusing the exact same app/DB.
/// </summary>
public sealed class MutableScriptedClassificationClient : IAiClassificationClient
{
    public AiClassificationResult Result { get; set; } = new("en", "Unsupported", new AiSearchFilters(null, null, null, null, false));

    public Task<AiClassificationResult> ClassifyAsync(string query, AiConversationHint? hint = null, CancellationToken ct = default) =>
        Task.FromResult(Result);
}

/// <summary>
/// The single most important test suite in this plan: <c>conversation_id</c> is a conversational
/// convenience ONLY, never an authorization artifact. Every scenario here proves that a stored
/// resolved-entity hint is independently re-checked against a FRESH <see cref="AiSearchAuthorizationService"/>
/// call on every turn -- never trusted on its own -- and that cross-tenant/cross-user/expired
/// conversation ids are silently (and safely) treated as absent.
/// </summary>
[Collection("sql")]
public class AiSearchConversationSecurityTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App(Action<IServiceCollection>? configureServices = null, Action<Microsoft.AspNetCore.Hosting.WebHostBuilderContext, Microsoft.Extensions.Configuration.IConfigurationBuilder>? configureAppConfig = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            if (configureServices is not null)
                b.ConfigureTestServices(configureServices);
            if (configureAppConfig is not null)
                b.ConfigureAppConfiguration(configureAppConfig);
        });

    /// Registers the mutable scripted classifier as a SINGLETON (so the same instance survives
    /// across the multiple HTTP requests / DI scopes a multi-turn test makes) and returns it so the
    /// test can change its Result between turns.
    private (WebApplicationFactory<Program> App, MutableScriptedClassificationClient Classifier) AppWithMutableClassifier(
        AiClassificationResult initial, Action<Microsoft.AspNetCore.Hosting.WebHostBuilderContext, Microsoft.Extensions.Configuration.IConfigurationBuilder>? configureAppConfig = null)
    {
        var client = new MutableScriptedClassificationClient { Result = initial };
        var app = App(s => s.AddSingleton<IAiClassificationClient>(client), configureAppConfig);
        return (app, client);
    }

    private static string Token(Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        return jwt.IssueAccess(userId, tenantId, [role], isPlatform: false);
    }

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenantId) =>
        AsUser(app, tenantId, Guid.NewGuid(), Policies.SchoolAdmin);

    private static HttpClient AsUser(
        WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(tenantId, userId, role));
        return client;
    }

    /// POSTs the search (optionally carrying a conversation_id) and returns the whole envelope.
    private static async Task<JsonElement> Search(
        HttpClient client, string query, string? conversationId = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await client.PostAsJsonAsync("/v1/ai/search", new { query, conversation_id = conversationId });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static string? ConversationId(JsonElement body) =>
        body.TryGetProperty("conversation_id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task Seed(Func<NpgsqlConnection, Task> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        await work(conn);
    }

    private async Task<T> Query<T>(Func<NpgsqlConnection, Task<T>> work)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'IsPlatform', @value=1");
        return await work(conn);
    }

    private static AiSearchFilters Filters(string? studentName = null) =>
        new(studentName, null, null, null, false);

    /// A teacher assigned (via TimetableSlots) to exactly one class, mirroring
    /// AiSearchSecurityTests.SeedTeacherOf.
    private async Task<Guid> SeedTeacherOf(Guid tenantId, Guid teacherUserId, string className)
    {
        var teacherId = Guid.NewGuid();
        await Seed(async conn =>
        {
            await conn.ExecuteAsync(
                "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            var classId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId) VALUES (@teacherUserId, @tenantId)",
                new { teacherUserId, tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, UserId) VALUES (@teacherId, @tenantId, N'Meena', @teacherUserId)",
                new { teacherId, tenantId, teacherUserId });
            await conn.ExecuteAsync(
                """
                INSERT dbo.Classes (Id, TenantId, Name, StudentCount, ClassTeacherId)
                VALUES (@classId, @tenantId, @className, 0, @teacherId)
                """,
                new { classId, tenantId, teacherId, className });
            await conn.ExecuteAsync(
                """
                INSERT dbo.TimetableSlots (TenantId, [Day], Period, Subject, ClassId, ClassName, TeacherId)
                VALUES (@tenantId, 'Mon', 1, N'Math', @classId, @className, @teacherId)
                """,
                new { tenantId, classId, teacherId, className });
        });
        return teacherId;
    }

    private async Task<Guid> InsertStudent(
        Guid tenantId, string name, string grade, string section, string classLabel)
    {
        var id = Guid.NewGuid();
        var admissionNo = $"ADM-{Guid.NewGuid():N}"[..20];
        await Seed(conn => conn.ExecuteAsync(
            """
            INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Grade, Section, ClassLabel, Status)
            VALUES (@id, @tenantId, @admissionNo, @name, @grade, @section, @classLabel, N'active')
            """,
            new { id, tenantId, admissionNo, name, grade, section, classLabel }));
        return id;
    }

    private async Task MoveStudent(Guid studentId, string grade, string section, string classLabel) =>
        await Seed(conn => conn.ExecuteAsync(
            "UPDATE dbo.Students SET Grade = @grade, Section = @section, ClassLabel = @classLabel WHERE Id = @studentId",
            new { studentId, grade, section, classLabel }));

    /// <summary>
    /// A teacher resolves a student ("Rahul") who is, at that moment, genuinely inside a class the
    /// teacher teaches. The student is then moved to a class the teacher does NOT teach. The teacher's
    /// bare follow-up ("kya padhate hain?"-shaped: no name, same conversation_id) must fail closed --
    /// re-authorization re-derives the teacher's CURRENT class scope from scratch and finds the student
    /// no longer inside it, so no_match is returned and the student's new class never leaks.
    /// </summary>
    [Fact]
    public async Task A_teachers_follow_up_fails_closed_after_the_student_changes_class()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SeedTeacherOf(tenantId, teacherUserId, "8A");
        var rahulId = await InsertStudent(tenantId, "Rahul Verma", "8", "A", "8A");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul")));
        await using var _ = app;
        var teacher = AsUser(app, tenantId, teacherUserId, Policies.Teacher);

        var turn1 = await Search(teacher, "Rahul kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        // Positive control: an IDENTICAL bare follow-up, still while Rahul is genuinely inside the
        // teacher's class, must succeed via the pre-resolved short-circuit. Without this, a bug that
        // always returned no_match for a nameless follow-up (never actually implementing pre-resolution)
        // would make the final assertion below pass for the wrong reason.
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn1b = await Search(teacher, "kya padhate hain?", conversationId);
        turn1b.GetProperty("status").GetString().Should().Be("success");

        // Rahul moves to a class this teacher does NOT teach.
        await MoveStudent(rahulId, "9", "B", "9B");

        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(teacher, "kya padhate hain?", conversationId);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("9B");
        turn2.GetRawText().Should().NotContain(rahulId.ToString());
    }

    /// <summary>
    /// A parent resolves their own linked child, then the ParentStudentLinks row is removed (the
    /// child is un-linked -- e.g. an admin correction). The parent's bare follow-up with the SAME
    /// conversation_id must fail closed: re-authorization re-derives the parent's CURRENT linked
    /// children from scratch and the (now-unlinked) child is no longer among them.
    /// </summary>
    [Fact]
    public async Task A_parents_follow_up_fails_closed_after_the_parent_child_link_is_removed()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Aisha")));
        await using var _ = app;

        var admin = Admin(app, tenantId);
        var parentEmail = $"mum{Guid.NewGuid():N}@home.test";
        var childRes = await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-CNV-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        });
        childRes.StatusCode.Should().Be(HttpStatusCode.Created, await childRes.Content.ReadAsStringAsync());
        var childBody = await childRes.Content.ReadFromJsonAsync<JsonElement>();
        var childId = childBody.GetProperty("data").GetProperty("id").GetGuid();

        var parentId = await Query(conn => conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@email)))
            """,
            new { email = parentEmail, tenantId }));

        var parent = AsUser(app, tenantId, parentId, Policies.StudentOrParent);
        var turn1 = await Search(parent, "Aisha kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        // Positive control: an IDENTICAL bare follow-up, still while the link genuinely exists, must
        // succeed via the pre-resolved short-circuit. Without this, a bug that always returned
        // no_match for a nameless follow-up (never actually implementing pre-resolution) would make
        // the final assertion below pass for the wrong reason.
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn1b = await Search(parent, "kaunsi class mein hai?", conversationId);
        turn1b.GetProperty("status").GetString().Should().Be("success");

        // Sever the parent-child link directly.
        await Seed(conn => conn.ExecuteAsync(
            "DELETE FROM dbo.ParentStudentLinks WHERE ParentUserId = @parentId AND StudentId = @childId",
            new { parentId, childId }));

        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(parent, "kaunsi class mein hai?", conversationId);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("Aisha");
        turn2.GetRawText().Should().NotContain(childId.ToString());
    }

    /// <summary>
    /// Final fix wave Finding I-1: a parent resolves their own linked child ("Aisha") in turn 1, then
    /// asks about a DIFFERENT, REAL person in the SAME tenant ("Rahul", a teacher, not their child) in
    /// turn 2 with the SAME conversation_id. AiSearchAuthorizationService's parent branch matches no
    /// linked child for "Rahul", nulls ClampedFilters.StudentName, AND sets NameUnmatched: true -- this
    /// must NOT be treated as a bare follow-up (which would wrongly fire pre-resolution against the
    /// STORED Aisha and answer about her). The pre-resolution guard must see NameUnmatched and refuse to
    /// treat this as a follow-up, so the parent gets no_match and neither Aisha's nor Rahul's data ever
    /// appears in the response.
    /// </summary>
    [Fact]
    public async Task A_parents_question_about_a_different_real_person_is_no_match_not_the_stored_child()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Aisha")));
        await using var _ = app;

        var admin = Admin(app, tenantId);
        var parentEmail = $"mum{Guid.NewGuid():N}@home.test";
        var childRes = await admin.PostAsJsonAsync("/v1/students", new
        {
            admission_no = $"ADM-I1-{Guid.NewGuid():N}"[..20],
            name = "Aisha Khan",
            grade = "IV",
            section = "B",
            roll = 1,
            guardian_email = parentEmail,
        });
        childRes.StatusCode.Should().Be(HttpStatusCode.Created, await childRes.Content.ReadAsStringAsync());
        var childBody = await childRes.Content.ReadFromJsonAsync<JsonElement>();
        var childId = childBody.GetProperty("data").GetProperty("id").GetGuid();

        var parentId = await Query(conn => conn.QuerySingleAsync<Guid>(
            """
            SELECT Id FROM dbo.Users
            WHERE TenantId = @tenantId AND LOWER(LTRIM(RTRIM(Email))) = LOWER(LTRIM(RTRIM(@email)))
            """,
            new { email = parentEmail, tenantId }));

        // A real, distinct person ("Rahul Sharma" the teacher) seeded in the same tenant -- not linked
        // to this parent at all -- so the repro is non-vacuous: "Rahul" genuinely exists and genuinely
        // resolves to someone, just not someone this parent may see.
        Guid teacherId = default;
        await Seed(async conn =>
        {
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, SubjectsCsv) VALUES (@teacherId, @tenantId, N'Rahul Sharma', N'Mathematics')",
                new { teacherId, tenantId });
        });

        var parent = AsUser(app, tenantId, parentId, Policies.StudentOrParent);
        var turn1 = await Search(parent, "Aisha kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        // Turn 2: a real person's name is asked for, but it matches none of this parent's linked
        // children -- AuthorizeAsync's parent branch sets NameUnmatched: true and nulls StudentName.
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul"));
        var turn2 = await Search(parent, "Who is Rahul?", conversationId);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("Aisha", "the stored child must never be substituted for the person actually asked about");
        turn2.GetRawText().Should().NotContain(childId.ToString());
        turn2.GetRawText().Should().NotContain("Rahul Sharma", "an unmatched name must never leak the real other person's data either");
        turn2.GetRawText().Should().NotContain("Mathematics");
        turn2.GetRawText().Should().NotContain(teacherId.ToString());
    }

    /// <summary>
    /// A conversation_id minted under tenant A is submitted, unchanged, by tenant B's admin.
    /// AiConversationContextStore.LoadAsync is scoped by the AMBIENT (JWT-derived) tenant id, so this
    /// must silently find no row -- a completely fresh classification happens (proved by scripting a
    /// distinguishing response for turn 2), and tenant A's resolved entity never appears anywhere in
    /// tenant B's response.
    /// </summary>
    [Fact]
    public async Task A_conversation_id_from_a_different_tenant_is_silently_treated_as_absent()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantA, tier: "platinum");
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantB, tier: "platinum");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul")));
        await using var _ = app;

        Guid teacherIdInA = default;
        await Seed(async conn =>
        {
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantA", new { tenantA });
            teacherIdInA = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name) VALUES (@teacherIdInA, @tenantA, N'Rahul Sharma')",
                new { teacherIdInA, tenantA });
        });

        var turn1 = await Search(Admin(app, tenantA), "Rahul kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationIdFromA = ConversationId(turn1);
        conversationIdFromA.Should().NotBeNull();

        // Tenant B's admin submits tenant A's conversation_id with a bare follow-up -- scripted as a
        // genuine PersonLookup (not "Unsupported") so this turn actually routes through AuthorizeAsync
        // and the re-authorization-of-stored-entity block, proving LoadAsync's tenant scoping for real
        // rather than short-circuiting before authorization ever runs.
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(Admin(app, tenantB), "kya padhate hain?", conversationIdFromA);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("Rahul");
        turn2.GetRawText().Should().NotContain(teacherIdInA.ToString());
    }

    /// <summary>
    /// Same shape as tenant isolation, but same tenant, different user: a conversation_id minted for
    /// one admin is submitted by a DIFFERENT user in the SAME tenant. AiConversationContextStore keys
    /// on (conversationId, tenantId, userId) together, so this must also be silently treated as absent.
    /// </summary>
    [Fact]
    public async Task A_conversation_id_from_a_different_user_in_the_same_tenant_is_silently_treated_as_absent()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul")));
        await using var _ = app;

        Guid teacherRowId = default;
        await Seed(async conn =>
        {
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            teacherRowId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name) VALUES (@teacherRowId, @tenantId, N'Rahul Sharma')",
                new { teacherRowId, tenantId });
        });

        var userOneId = Guid.NewGuid();
        var turn1 = await Search(AsUser(app, tenantId, userOneId, Policies.SchoolAdmin), "Rahul kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        var userTwoId = Guid.NewGuid();
        // Scripted as a genuine PersonLookup bare follow-up (not "Unsupported") so this turn actually
        // routes through AuthorizeAsync and the re-authorization-of-stored-entity block, proving
        // LoadAsync's (tenantId, userId) scoping for real rather than short-circuiting before
        // authorization ever runs.
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(AsUser(app, tenantId, userTwoId, Policies.SchoolAdmin), "kya padhate hain?", conversationId);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("Rahul");
        turn2.GetRawText().Should().NotContain(teacherRowId.ToString());
    }

    /// <summary>
    /// An explicit "Hindi mein batao" directive (languageDirective="hi") on turn 1 must stick across a
    /// later, per-turn-detected-as-English follow-up that carries no directive of its own -- the
    /// override is stored and re-applied every subsequent turn until superseded.
    /// </summary>
    [Fact]
    public async Task An_explicit_Hindi_mein_batao_directive_sticks_across_a_later_English_shaped_follow_up()
    {
        var tenantId = Guid.NewGuid();
        var teacherUserId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await SeedTeacherOf(tenantId, teacherUserId, "8A");
        await InsertStudent(tenantId, "Rahul Verma", "8", "A", "8A");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul"), "hi"));
        await using var _ = app;
        var teacher = AsUser(app, tenantId, teacherUserId, Policies.Teacher);

        var turn1 = await Search(teacher, "Rahul kaun hai? Hindi mein batao.");
        turn1.GetProperty("language").GetString().Should().Be("hi");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        // Turn 2: no new directive, and the per-turn detection itself says "en".
        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(teacher, "kya padhate hain?", conversationId);

        turn2.GetProperty("language").GetString().Should().Be("hi");
    }

    /// <summary>
    /// Two same-named people ("Rahul Sharma" the teacher, "Rahul Verma" the student) force a
    /// clarification. Restating the full name resolves it once; further bare follow-ups with the SAME
    /// conversation_id then resolve against the STORED resolved entity (the teacher), never re-running
    /// a fresh ambiguous name search that would clarify again.
    /// </summary>
    [Fact]
    public async Task Disambiguation_then_a_follow_up_resolves_against_the_stored_candidates_not_a_fresh_search()
    {
        var tenantId = Guid.NewGuid();
        Guid teacherId = default;
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        await Seed(async conn =>
        {
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name, SubjectsCsv) VALUES (@teacherId, @tenantId, N'Rahul Sharma', N'Mathematics')",
                new { teacherId, tenantId });
        });
        await InsertStudent(tenantId, "Rahul Verma", "8", "A", "8A");

        var (app, classifier) = AppWithMutableClassifier(
            new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul")));
        await using var _ = app;
        var admin = Admin(app, tenantId);

        var turn1 = await Search(admin, "Rahul kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("needs_clarification");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul Sharma"));
        var turn2 = await Search(admin, "Rahul Sharma", conversationId);
        turn2.GetProperty("status").GetString().Should().Be("success");
        turn2.GetProperty("answer").GetString().Should().Contain("Mathematics");

        classifier.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn3 = await Search(admin, "kya padhate hain?", conversationId);
        turn3.GetProperty("status").GetString().Should().Be("success");
        turn3.GetProperty("answer").GetString().Should().Contain("Mathematics");

        // Turn 4: a further bare follow-up must still resolve against the SAME stored teacher (not
        // re-run an ambiguous fresh search that would clarify again between "Rahul Sharma"/"Rahul
        // Verma") -- PersonLookupHandler's teacher rendering always reports subjects, so this proves
        // repeated pre-resolved use across multiple turns rather than a fresh disambiguation.
        var turn4 = await Search(admin, "aur kuch?", conversationId);
        turn4.GetProperty("status").GetString().Should().Be("success");
        turn4.GetProperty("answer").GetString().Should().Contain("Mathematics");
    }

    /// <summary>
    /// A resolved conversation whose TTL has already elapsed (configured to 0 minutes here) must fall
    /// back to a fresh classification with NO error -- and the fresh classification's own result is
    /// what is returned, proving the old context was genuinely dropped rather than silently reused.
    /// </summary>
    [Fact]
    public async Task An_expired_conversation_id_falls_back_to_a_fresh_query_with_no_error()
    {
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");

        var client = new MutableScriptedClassificationClient
        {
            Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: "Rahul")),
        };
        await using var app = App(
            s => s.AddSingleton<IAiClassificationClient>(client),
            (_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiSearch:ConversationContextTtlMinutes"] = "0",
            }));

        Guid teacherId = default;
        await Seed(async conn =>
        {
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            teacherId = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT dbo.Teachers (Id, TenantId, Name) VALUES (@teacherId, @tenantId, N'Rahul Sharma')",
                new { teacherId, tenantId });
        });

        var admin = Admin(app, tenantId);
        var turn1 = await Search(admin, "Rahul kaun hai?");
        turn1.GetProperty("status").GetString().Should().Be("success");
        var conversationId = ConversationId(turn1);
        conversationId.Should().NotBeNull();

        // A bare follow-up on the SAME conversation_id, scripted as a genuine PersonLookup (not
        // "Unsupported") so this turn actually routes through AuthorizeAsync and the
        // re-authorization-of-stored-entity block. Because the TTL-expired row was deleted by
        // LoadAsync, storedContext is null, so there is no pre-resolved entity id to hand the
        // handler -- a nameless PersonLookup with nothing pre-resolved must report no_match, proving
        // the old context was genuinely dropped rather than silently reused.
        client.Result = new AiClassificationResult("en", "PersonLookup", Filters(studentName: null));
        var turn2 = await Search(admin, "something else entirely", conversationId);

        turn2.GetProperty("status").GetString().Should().Be("no_match");
        turn2.GetRawText().Should().NotContain("Rahul");
    }
}
