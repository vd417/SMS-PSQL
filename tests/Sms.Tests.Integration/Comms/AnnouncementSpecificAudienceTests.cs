using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Comms;

[Collection("sql")]
public class AnnouncementSpecificAudienceTests(PostgresFixture fx)
{
    private const string Key = "integration-test-signing-key-32-bytes-min!!";

    private sealed class CapturingQueue : IEmailQueue
    {
        public List<EmailMessage> Items { get; } = [];
        public void Enqueue(EmailMessage message) => Items.Add(message);
        // EmailDispatchWorker's background loop calls this immediately on host start;
        // block until shutdown instead of throwing, or an unhandled exception here
        // crashes the whole test host.
        public async ValueTask<EmailMessage> DequeueAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }
    }

    [Fact]
    public async Task Audience_specific_emails_only_the_explicit_recipients_not_the_whole_roster()
    {
        var queue = new CapturingQueue();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureTestServices(services => services.AddSingleton<IEmailQueue>(queue));
        });
        var tenantId = Guid.NewGuid();
        var principalUserId = Guid.NewGuid();

        await using (var conn = new Npgsql.NpgsqlConnection(fx.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("SELECT set_config('app.tenant_id', @tenantId::text, false)", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
            // Two OTHER students in the tenant whose emails must NOT be swept in by "specific".
            await conn.ExecuteAsync(
                "INSERT dbo.Students (Id, TenantId, AdmissionNo, Name, Status, Email, GuardianEmail) VALUES " +
                "(@id1, @tenantId, 'A1', 'Other One', 'active', 'other1@school.test', 'guardian1@school.test'), " +
                "(@id2, @tenantId, 'A2', 'Other Two', 'active', 'other2@school.test', 'guardian2@school.test')",
                new { id1 = Guid.NewGuid(), id2 = Guid.NewGuid(), tenantId });
        }

        var jwt = new JwtTokenService(
            new JwtOptions { Issuer = "sms", Audience = "sms-apps", SigningKey = Key, AccessTokenMinutes = 15 },
            new SystemClock());
        var token = jwt.IssueAccess(principalUserId, tenantId, new[] { Policies.Principal }, isPlatform: false);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var create = await client.PostAsJsonAsync("/v1/announcements", new
        {
            title = "Fee invoice generated",
            body = "A Term 2 fee invoice of 8500 has been generated. Due date: 2026-03-15.",
            type = "fee_invoice",
            audience = "specific",
            emails = new[] { "guardian-aarav@school.test" },
            channels = new[] { "email" },
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        queue.Items.Should().ContainSingle();
        queue.Items[0].To.Should().Be("guardian-aarav@school.test");
    }
}
