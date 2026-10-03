using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.DAO;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

// B5: per-tenant email/phone uniqueness is enforced across student/teacher/staff create+update,
// authoritatively inside the create/update procs (which PERFORM dbo.contact_claims_sync) and
// surfaced as the existing friendly `conflict` (HTTP 409) at the service boundary (a proc that
// raises the SMSDC sentinel is mapped to ContactConflictException by BaseRepository).
//
// The create/update HTTP flows carry no PersonId (null at create time), so the owner-reuse path
// (Review Focus #2 — "uninvited profile then invited with the SAME person") can only be expressed
// where a shared PersonId is actually passed; that scenario is pinned at the DAO level via
// IContactValidator, mirroring ContactValidatorTests.
[Collection("sql")]
public class ContactUniquenessTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
        });

    private static HttpClient AdminClient(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, ["school.admin"], isPlatform: false);
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private static object TeacherBody(string? email, string? phone, string name = "T") => new
    {
        name, gender = "M", department = "Science", designation = "Teacher",
        subjects = Array.Empty<string>(), phone, email,
        exp = 1, rating = 4.0, result = 80, load = 10, avatar_hue = 100, top = false,
    };

    private static object StaffBody(string? email, string? phone, string name = "S") => new
    {
        name, gender = "M", role = "Clerk", category = "office", department = "Admin",
        phone, email, shift = "Morning", route = (string?)null, avatar_hue = 100,
    };

    private static async Task<Guid> CreatedId(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    // create Teacher a@x.com, then Staff a@x.com in the SAME tenant -> 409 conflict.
    [Fact]
    public async Task Teacher_then_staff_same_email_same_tenant_conflicts()
    {
        var tid = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tid, tier: "platinum");
        await using var app = App();
        var admin = AdminClient(app, tid);

        (await admin.PostAsJsonAsync("/v1/teachers", TeacherBody("a@x.com", null)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var staff = await admin.PostAsJsonAsync("/v1/staff", StaffBody("a@x.com", null));

        staff.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // the same email in ANOTHER tenant is allowed (claims are per-tenant).
    [Fact]
    public async Task Teacher_same_email_in_another_tenant_is_allowed()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        await using var app = App();

        (await AdminClient(app, t1).PostAsJsonAsync("/v1/teachers", TeacherBody("shared@x.com", null)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await AdminClient(app, t2).PostAsJsonAsync("/v1/teachers", TeacherBody("shared@x.com", null)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // editing one teacher's phone to a phone another person already holds -> 409
    // (phone normalization: "+91 90000 11111" and "9000011111" are the same claim).
    [Fact]
    public async Task Edit_teacher_phone_to_another_persons_phone_conflicts()
    {
        var tid = Guid.NewGuid();
        await using var app = App();
        var admin = AdminClient(app, tid);

        await CreatedId(await admin.PostAsJsonAsync("/v1/teachers", TeacherBody("t1@x.com", "+91 90000 11111", "One")));
        var id2 = await CreatedId(await admin.PostAsJsonAsync("/v1/teachers", TeacherBody("t2@x.com", "+91 90000 22222", "Two")));

        var patch = await admin.PatchAsJsonAsync($"/v1/teachers/{id2}", new { phone = "9000011111" });

        patch.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // Review Focus #3: two people with NO email/phone never collide (NULL reserves nothing).
    [Fact]
    public async Task Two_people_with_null_contact_in_same_tenant_are_allowed()
    {
        var tid = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tid, tier: "platinum");
        await using var app = App();
        var admin = AdminClient(app, tid);

        (await admin.PostAsJsonAsync("/v1/teachers", TeacherBody(null, null, "NoContact1")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync("/v1/staff", StaffBody(null, null, "NoContact2")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private ContactValidator Validator(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Set(tenantId, null, isPlatform: false);
        return new ContactValidator(new NpgsqlConnectionFactory(fx.ConnectionString, ctx));
    }

    // Review Focus #2: an uninvited teacher profile claims b@x.com; inviting a login for the SAME
    // person (same PersonId) reuses that claim rather than raising a false conflict.
    [Fact]
    public async Task Uninvited_profile_then_login_for_the_same_person_reuses_the_claim()
    {
        var tid = Guid.NewGuid();
        var person = Guid.NewGuid();
        var v = Validator(tid);

        (await v.SyncAsync(tid, "teacher", "T1", person, "b@x.com", null)).IsValid.Should().BeTrue();

        var login = await v.SyncAsync(tid, "user", "U1", person, "b@x.com", null);

        login.IsValid.Should().BeTrue();
        login.ConflictKind.Should().BeNull();
    }

    // A genuinely different person claiming the same contact is a conflict (control for the reuse case).
    [Fact]
    public async Task Different_person_claiming_the_same_contact_conflicts()
    {
        var tid = Guid.NewGuid();
        var v = Validator(tid);

        (await v.SyncAsync(tid, "teacher", "T1", Guid.NewGuid(), "c@x.com", null)).IsValid.Should().BeTrue();

        var other = await v.SyncAsync(tid, "staff", "S1", Guid.NewGuid(), "c@x.com", null);

        other.IsValid.Should().BeFalse();
        other.ConflictKind.Should().Be(ContactConflictKind.Email);
    }
}
