using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Application.Common;
using Sms.Application.Services.Comms;
using Sms.Modules.Comms;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Finance;

[Collection("sql")]
public class GenerateInvoicesNotifyTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingAnnouncementService : IAnnouncementService
    {
        public List<CreateAnnouncementRequest> Created { get; } = [];

        public Task<ApiResult<IReadOnlyList<AnnouncementResponse>>> ListAsync(string? audience, CancellationToken ct = default) =>
            Task.FromResult(ApiResult<IReadOnlyList<AnnouncementResponse>>.Ok(Array.Empty<AnnouncementResponse>()));

        public Task<ApiResult<AnnouncementResponse>> CreateAsync(
            CreateAnnouncementRequest req, Guid? creatorUserId, string? role, CancellationToken ct = default)
        {
            Created.Add(req);
            return Task.FromResult(ApiResult<AnnouncementResponse>.Ok(
                new AnnouncementResponse(Guid.NewGuid(), Guid.Empty, req.Title, req.Body, DateTime.UtcNow, null, role, req.Type ?? "general", false, req.Audience)));
        }
    }

    private static async Task<(Guid tenantId, Guid principalUserId, Guid studentId)> SeedAsync(PostgresFixture fx)
    {
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        await using var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
            new { principalUserId, tenantId });
        await conn.ExecuteAsync(
            "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Grade, GuardianEmail, GuardianPhone) " +
            "VALUES (@studentId, @tenantId, 'A100', 'Aarav Sharma', 'active', '5', 'guardian-aarav@school.test', '+91-9000000000')",
            new { studentId, tenantId });

        return (tenantId, principalUserId, studentId);
    }

    private static HttpClient AuthedClient(WebApplicationFactory<Program> app, Guid tenantId, Guid userId, string role)
    {
        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(userId, tenantId, new[] { role }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GenerateInvoices_never_fires_a_notification_invoice_creation_is_not_a_payment_event()
    {
        var fake = new CapturingAnnouncementService();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddScoped<IAnnouncementService>(_ => fake));
        });

        var (tenantId, principalUserId, _) = await SeedAsync(fx);
        var client = AuthedClient(app, tenantId, principalUserId, Policies.Principal);

        var upsert = await client.PutAsJsonAsync("/v1/fees/structure", new
        {
            name = "Standard",
            academic_year = "2026",
            currency = "INR",
            effective_from = "2026-01-01",
            status = "active",
            amounts = new Dictionary<string, object> { ["5"] = new Dictionary<string, object> { ["Tuition"] = 8500 } },
        });
        upsert.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Created.Clear(); // the structure-activation notification (Task 3) is not what this test covers

        var generate = await client.PostAsJsonAsync("/v1/fees/invoices/generate", new
        {
            academic_year = "2026",
            term = "Term 2",
            grades = new[] { "5" },
            due_date = "2026-03-15",
        });
        generate.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.Created.Should().BeEmpty();
    }
}
