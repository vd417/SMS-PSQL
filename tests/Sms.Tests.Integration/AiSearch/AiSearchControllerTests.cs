using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Services.AiSearch;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.AiSearch;

/// The single POST /v1/ai/search endpoint wired on top of the already-tested AiSearchService
/// orchestrator. These tests only pin the HTTP-layer contract (auth, status-code mapping for
/// each AiSearchError.Code, JSON casing) -- the orchestrator's own behaviour (classification,
/// authorization clamping, per-intent handlers) is covered by AiSearchService/-handler tests.
[Collection("sql")]
public class AiSearchControllerTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private WebApplicationFactory<Program> App(Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            if (configureServices is not null)
                b.ConfigureTestServices(configureServices);
        });

    private static HttpClient Admin(WebApplicationFactory<Program> app, Guid tenantId)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(Guid.NewGuid(), tenantId, [Policies.SchoolAdmin], isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        await using var app = App();
        var anon = app.CreateClient();

        var res = await anon.PostAsJsonAsync("/v1/ai/search", new { query = "Aaj kitne bachche aaye?" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Silver_tenant_gets_a_feature_locked_response()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "silver");
        var admin = Admin(app, tenantId);

        var res = await admin.PostAsJsonAsync("/v1/ai/search", new { query = "Aaj kitne bachche aaye?" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("FeatureNotEnabled");
    }

    /// AI Search is a Platinum-exclusive feature (see TierFeatures) — Gold no longer grants it, and no
    /// role (not even school.admin) can bypass the tenant's plan tier.
    [Fact]
    public async Task Gold_tenant_gets_a_feature_locked_response()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "gold");
        var admin = Admin(app, tenantId);

        var res = await admin.PostAsJsonAsync("/v1/ai/search", new { query = "Aaj kitne bachche aaye?" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("FeatureNotEnabled");
    }

    [Fact]
    public async Task Empty_query_returns_a_400_style_InvalidRequest()
    {
        await using var app = App();
        var tenantId = Guid.NewGuid();
        await TestTenancy.EnsureTenantAsync(fx.ConnectionString, tenantId, tier: "platinum");
        var admin = Admin(app, tenantId);

        var res = await admin.PostAsJsonAsync("/v1/ai/search", new { query = "" });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("InvalidRequest");
    }

    // Pins the snake_case JSON naming policy end-to-end, in both directions: the request body's
    // "page_size" field must bind to AiSearchRequest.PageSize (not silently ignored, which is what
    // would happen if the policy ever regressed to camelCase, since ASP.NET Core model binding is
    // case-insensitive for property NAMES but not for the underscore itself), and the response's
    // PageSize must serialize back out as "page_size". The real AiSearchService/classifier chain is
    // swapped out for a capturing fake here -- exercising it for real would require a live LLM
    // classification call, which is exactly the kind of external dependency this HTTP-layer test
    // suite (see the file banner) is not meant to reach through.
    [Fact]
    public async Task Snake_case_page_size_in_the_request_body_binds_to_PageSize()
    {
        var capturing = new CapturingAiSearchService();
        await using var app = App(services => services.AddSingleton<IAiSearchService>(capturing));
        var tenantId = Guid.NewGuid();
        var admin = Admin(app, tenantId);

        var res = await admin.PostAsJsonAsync(
            "/v1/ai/search", new { query = "some query", page_size = 5 });
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        capturing.LastRequest.Should().NotBeNull();
        capturing.LastRequest!.PageSize.Should().Be(5);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("page_size").GetInt32().Should().Be(5);
    }

    private sealed class CapturingAiSearchService : IAiSearchService
    {
        public AiSearchRequest? LastRequest { get; private set; }

        public Task<AiSearchResponse> SearchAsync(
            AiSearchRequest request, IReadOnlyList<string> callerRoles, CancellationToken ct = default)
        {
            LastRequest = request;
            var pageSize = request.PageSize ?? 20;
            return Task.FromResult(AiSearchResponse.Ok(
                "en", "StudentSearch", "ok", data: null,
                page: request.Page ?? 1, pageSize: pageSize, count: 0, hasNextPage: false));
        }
    }

    /// Finding 4: the "ai-search" rate-limit policy partitions on http.User.FindFirst("sub"), which
    /// requires UseRateLimiter to run AFTER UseAuthentication — otherwise HttpContext.User is always
    /// the unauthenticated principal at partition-key time and every caller silently falls back to
    /// per-IP partitioning (an entire school behind one NAT gateway sharing a single budget). Both
    /// callers below share the same client/IP (TestServer); if partitioning were still per-IP, user
    /// B's request would also be rejected once user A exhausts the (permit=1) budget.
    [Fact]
    public async Task Two_different_authenticated_users_get_independent_rate_limit_budgets_not_a_shared_per_IP_one()
    {
        var capturing = new CapturingAiSearchService();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.UseSetting("RateLimiting:AiSearchPermitPerMinute", "1");
            b.ConfigureTestServices(services => services.AddSingleton<IAiSearchService>(capturing));
        });
        var tenantId = Guid.NewGuid();
        var userA = Admin(app, tenantId);
        var userB = Admin(app, tenantId);

        var first = await userA.PostAsJsonAsync("/v1/ai/search", new { query = "first" });
        var second = await userA.PostAsJsonAsync("/v1/ai/search", new { query = "second" });
        var fromUserB = await userB.PostAsJsonAsync("/v1/ai/search", new { query = "first for B" });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "user A's own single-request budget is exhausted");
        fromUserB.StatusCode.Should().Be(HttpStatusCode.OK,
            "user B has their own, independent budget — proving the partition key is the " +
            "authenticated user's sub claim, not the shared client IP");
    }
}
