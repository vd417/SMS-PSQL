using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sms.Shared.Kernel.Push;
using Sms.Tests.Unit.Routing; // reuse StubHttpMessageHandler + SingleClientHttpClientFactory
using Xunit;

namespace Sms.Tests.Unit.Push;

public class ExpoPushSenderTests
{
    private static ExpoPushSender Sender(StubHttpMessageHandler handler, ExpoPushOptions? opts = null) =>
        new(new SingleClientHttpClientFactory("expo", new HttpClient(handler)),
            Options.Create(opts ?? new ExpoPushOptions()), NullLogger<ExpoPushSender>.Instance);

    [Fact]
    public async Task Posts_one_message_to_the_expo_endpoint_with_tokens_title_body()
    {
        string? path = null, body = null;
        var handler = new StubHttpMessageHandler((req, b) =>
        {
            path = req.RequestUri!.AbsolutePath; body = b;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Sender(handler).SendAsync(["ExponentPushToken[a]"], "Bus near stop", "Bus #7 is close.");

        path.Should().Be("/--/api/v2/push/send");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("to").EnumerateArray().Should().ContainSingle();
        doc.RootElement.GetProperty("title").GetString().Should().Be("Bus near stop");
        doc.RootElement.GetProperty("body").GetString().Should().Be("Bus #7 is close.");
    }

    [Fact]
    public async Task Chunks_more_than_the_batch_size_into_multiple_requests()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var tokens = Enumerable.Range(0, 150).Select(i => $"ExponentPushToken[{i}]").ToList();

        await Sender(handler, new ExpoPushOptions { MaxBatchSize = 100 }).SendAsync(tokens, "t", "b");

        requestCount.Should().Be(2); // 100 + 50
    }

    [Fact]
    public async Task A_non_success_response_does_not_throw()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

        var act = () => Sender(handler).SendAsync(["ExponentPushToken[a]"], "t", "b");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_transport_exception_does_not_throw()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));

        var act = () => Sender(handler).SendAsync(["ExponentPushToken[a]"], "t", "b");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_empty_token_list_makes_no_http_call()
    {
        var called = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        await Sender(handler).SendAsync([], "t", "b");

        called.Should().BeFalse();
    }
}
