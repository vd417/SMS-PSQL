using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using Sms.Api.Logging;
using Xunit;

namespace Sms.Tests.Integration.Logging;

public class SensitiveQueryRedactorTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl";

    [Theory]
    [InlineData("?access_token=" + Jwt, "?access_token=[REDACTED]")]
    [InlineData("https://api.example/hubs/live?access_token=" + Jwt, "https://api.example/hubs/live?access_token=[REDACTED]")]
    [InlineData("/hubs/live?id=abc&access_token=" + Jwt + "&x=1", "/hubs/live?id=abc&access_token=[REDACTED]&x=1")]
    [InlineData("GET https://h/hubs/live?access_token=" + Jwt + " - 401 0 null 3ms", "GET https://h/hubs/live?access_token=[REDACTED] - 401 0 null 3ms")]
    [InlineData("?ACCESS_TOKEN=" + Jwt, "?ACCESS_TOKEN=[REDACTED]")]
    [InlineData("?access%5Ftoken=" + Jwt, "?access%5Ftoken=[REDACTED]")]
    [InlineData("?refresh_token=r1&password=p1&api_key=k1", "?refresh_token=[REDACTED]&password=[REDACTED]&api_key=[REDACTED]")]
    [InlineData("access_token=" + Jwt, "access_token=[REDACTED]")]
    [InlineData("?access_token=" + Jwt + "#frag", "?access_token=[REDACTED]#frag")]
    public void Sensitive_values_are_masked(string input, string expected)
    {
        var output = SensitiveQueryRedactor.Redact(input);
        output.Should().Be(expected);
        output.Should().NotContain(Jwt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/hubs/live")]
    [InlineData("?page=2&size=50&q=token")]
    [InlineData("?tokenizer=abc&my_access_token_count=3")]
    [InlineData("?access_token=")]
    [InlineData("/v1/exam-papers/00000000-0000-0000-0000-000000000001")]
    [InlineData("Platform metrics snapshot upserted for the current month.")]
    public void Non_sensitive_text_is_unchanged(string input) =>
        SensitiveQueryRedactor.Redact(input).Should().Be(input);

    [Fact]
    public void Enricher_masks_the_query_string_in_the_rendered_hosting_message_and_keeps_other_params()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With<SensitiveQueryRedactionEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Same shape as Microsoft.AspNetCore.Hosting's "Request starting" event.
        logger.Information("Request starting {Protocol} {Method} {Scheme}://{Host}{PathBase}{Path}{QueryString} - {ContentType} {ContentLength}",
            "HTTP/1.1", "GET", "https", "api.example", "", "/hubs/live", $"?access_token={Jwt}&probe=keep-me", null, null);

        var e = sink.Events.Should().ContainSingle().Subject;
        var rendered = Literal(e);
        rendered.Should().NotContain(Jwt);
        rendered.Should().Contain("/hubs/live?access_token=[REDACTED]&probe=keep-me");
        e.Properties["QueryString"].ToString().Should().NotContain(Jwt);
        e.Properties["Path"].ToString().Should().Be("\"/hubs/live\"");
    }

    // How the console sink prints it: strings unquoted.
    private static string Literal(LogEvent e)
    {
        var w = new StringWriter();
        new MessageTemplateTextFormatter("{Message:l}").Format(e, w);
        return w.ToString();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
