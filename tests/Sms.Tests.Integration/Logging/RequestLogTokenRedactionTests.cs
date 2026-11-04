using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Time;
using Xunit;

namespace Sms.Tests.Integration.Logging;

/// End-to-end through the real Program pipeline: ASP.NET Core hosting diagnostics, Serilog request
/// logging and JWT bearer auth, with an extra sink capturing exactly what the console would get.
[Collection("sql")]
public class RequestLogTokenRedactionTests(PostgresFixture fx)
{
    private const string Key = "test-signing-key-at-least-32-bytes-long!!";

    private static WebApplicationFactory<Program> App(PostgresFixture fx, CapturingSink sink) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("environment", "Production");
            b.UseSetting("ConnectionStrings:Sql", fx.ConnectionString);
            b.UseSetting("Jwt:SigningKey", Key);
            b.ConfigureServices(s => s.AddSingleton<ILogEventSink>(sink));
        });

    private static string IssueToken() =>
        new JwtTokenService(new JwtOptions { SigningKey = Key }, new SystemClock())
            .IssueAccess(Guid.NewGuid(), Guid.NewGuid(), ["school.owner"], isPlatform: false);

    [Fact]
    public async Task Hub_query_token_is_accepted_but_never_written_to_the_logs()
    {
        var sink = new CapturingSink();
        await using var app = App(fx, sink);
        var token = IssueToken();

        var res = await app.CreateClient().GetAsync($"/hubs/live?access_token={token}&probe=keep-me");

        // Auth unchanged: the query token still authenticates hub requests (no challenge).
        res.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        var finished = await sink.WaitForAsync(e => CapturingSink.Literal(e).StartsWith("Request finished"));
        sink.AllText().Should().NotContain(token);
        sink.AllText().Should().NotContain(token.Split('.')[2], "not even the signature may leak");
        CapturingSink.Literal(finished).Should().Contain("/hubs/live?access_token=[REDACTED]&probe=keep-me");
        sink.Rendered().Should().Contain(m => m.StartsWith("Request starting") && m.Contains("access_token=[REDACTED]"));
        // Normal Serilog request logging still runs.
        sink.Rendered().Should().Contain(m => m.StartsWith("HTTP GET /hubs/live responded"));
    }

    [Fact]
    public async Task Invalid_hub_query_token_is_still_rejected_and_not_logged()
    {
        var sink = new CapturingSink();
        await using var app = App(fx, sink);
        const string bogus = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJub2JvZHkifQ.bm90LWEtcmVhbC1zaWduYXR1cmU";

        var res = await app.CreateClient().GetAsync($"/hubs/transport-fleet?access_token={bogus}");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await sink.WaitForAsync(e => CapturingSink.Literal(e).StartsWith("Request finished"));
        sink.AllText().Should().NotContain(bogus);
        sink.AllText().Should().NotContain("bm90LWEtcmVhbC1zaWduYXR1cmU");
    }

    [Fact]
    public async Task Hub_without_token_is_still_401()
    {
        await using var app = App(fx, new CapturingSink());
        var res = await app.CreateClient().GetAsync("/hubs/live");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Query_token_is_still_ignored_outside_hubs_while_header_auth_is_unchanged()
    {
        var sink = new CapturingSink();
        await using var app = App(fx, sink);
        var token = IssueToken();
        var client = app.CreateClient();

        (await client.GetAsync($"/v1/exam-papers?access_token={token}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var withHeader = new HttpRequestMessage(HttpMethod.Get, "/v1/exam-papers");
        withHeader.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(withHeader)).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        await sink.WaitForAsync(e => CapturingSink.Literal(e).StartsWith("Request finished") && CapturingSink.Literal(e).Contains("access_token"));
        sink.AllText().Should().NotContain(token);
    }

    public sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

        public IReadOnlyList<string> Rendered() => _events.Select(Literal).ToList();

        // How the console sink prints it: strings unquoted.
        public static string Literal(LogEvent e)
        {
            var w = new StringWriter();
            new MessageTemplateTextFormatter("{Message:l}").Format(e, w);
            return w.ToString();
        }

        /// Everything a sink could print: rendered messages, raw property values and exceptions.
        public string AllText() => string.Join("\n", _events.Select(e =>
            Literal(e) + " " + e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception));

        // "Request finished" is logged after the response has been handed back, so give it a moment.
        public async Task<LogEvent> WaitForAsync(Func<LogEvent, bool> match)
        {
            for (var i = 0; i < 50; i++)
            {
                var hit = _events.FirstOrDefault(match);
                if (hit is not null) return hit;
                await Task.Delay(100);
            }
            throw new TimeoutException("expected log event was not written");
        }
    }
}
