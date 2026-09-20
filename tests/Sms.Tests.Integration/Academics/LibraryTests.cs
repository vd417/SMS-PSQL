using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Sms.Tests.Integration;
using Xunit;

namespace Sms.Tests.Integration.Academics;

[Collection("sql")]
public class LibraryTests(PostgresFixture fx)
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

    [Fact]
    public async Task Issued_book_with_past_due_date_reads_as_overdue()
    {
        // THE KEY ASSERTION — proves overdue derivation
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var teacher = Client(app, tenantId, Policies.Teacher);

        // POST a book with status='issued' + a PAST due_date as principal → 201
        var book = await Data(await principal.PostAsJsonAsync("/v1/library", new
        {
            title = "The Great Gatsby",
            author = "F. Scott Fitzgerald",
            subject = "Literature",
            issued_to = "Alice Smith",
            due_date = "2024-01-01",   // well in the past
            status = "issued"
        }), HttpStatusCode.Created);

        book.GetProperty("title").GetString().Should().Be("The Great Gatsby");
        book.GetProperty("status").GetString().Should().Be("issued"); // stored as issued
        var bookId = book.GetProperty("id").GetGuid();

        // GET as teacher → that book's status reads 'overdue' (DERIVED in SQL)
        var list = await Data(await teacher.GetAsync("/v1/library"), HttpStatusCode.OK);
        list.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        JsonElement? found = null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == bookId) { found = item; break; }
        }
        found.Should().NotBeNull("created book should appear in the teacher's library list");
        found!.Value.GetProperty("status").GetString().Should().Be("overdue",
            "a book with status='issued' and a past due_date must be returned as 'overdue' by the derived SQL");
    }

    [Fact]
    public async Task Available_book_stays_available_on_get()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var principal = Client(app, tenantId, Policies.Principal);
        var teacher = Client(app, tenantId, Policies.Teacher);

        // POST an 'available' book
        var book = await Data(await principal.PostAsJsonAsync("/v1/library", new
        {
            title = "Clean Code",
            author = "Robert C. Martin",
            status = "available"
        }), HttpStatusCode.Created);

        var bookId = book.GetProperty("id").GetGuid();

        // GET as teacher → status stays 'available'
        var list = await Data(await teacher.GetAsync("/v1/library"), HttpStatusCode.OK);
        JsonElement? found = null;
        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("id").GetGuid() == bookId) { found = item; break; }
        }
        found.Should().NotBeNull();
        found!.Value.GetProperty("status").GetString().Should().Be("available",
            "a book with no due_date and status='available' must remain 'available'");
    }

    [Fact]
    public async Task Teacher_gets_403_on_post()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var teacher = Client(app, tenantId, Policies.Teacher);

        var postRes = await teacher.PostAsJsonAsync("/v1/library", new
        {
            title = "Some Book",
            author = "Some Author"
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StudentOrParent_gets_403_on_get_and_post()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        var student = Client(app, tenantId, Policies.StudentOrParent);

        var getRes = await student.GetAsync("/v1/library");
        getRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var postRes = await student.PostAsJsonAsync("/v1/library", new
        {
            title = "Another Book",
            author = "Another Author"
        });
        postRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
