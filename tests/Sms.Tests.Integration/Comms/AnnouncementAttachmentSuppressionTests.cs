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
public class AnnouncementAttachmentSuppressionTests(PostgresFixture fx)
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
    public async Task Supplying_an_attachment_suppresses_the_generic_notice_pdf()
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
            await conn.ExecuteAsync("EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId", new { tenantId });
            await conn.ExecuteAsync(
                "INSERT dbo.Users (Id, TenantId, Name) VALUES (@principalUserId, @tenantId, 'Priya Principal')",
                new { principalUserId, tenantId });
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
            emails = new[] { "someone@school.test" },
            channels = new[] { "email" },
            attachment_base64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            attachment_file_name = "receipt.pdf",
            attachment_content_type = "application/pdf",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        queue.Items.Should().ContainSingle();
        queue.Items[0].AttachmentFileName.Should().Be("receipt.pdf");
        queue.Items[0].ExtraAttachments.Should().BeNullOrEmpty();
    }
}
