using System.Net;
using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Xunit;

namespace Sms.Tests.Unit.Auth;

public class Fast2SmsWhatsAppOtpSenderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"return":true,"request_id":"abc"}"""));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static Fast2SmsWhatsAppOtpSender Sender(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://www.fast2sms.com/") }, new WhatsAppOtpOptions
        {
            ApiKey = "test-key",
            PhoneNumberId = "1420447271151765",
            MessageId = "41731",
        });

    [Fact]
    public async Task Send_posts_one_variable_to_the_approved_template_and_returns_the_code()
    {
        var handler = new StubHandler();
        var code = await Sender(handler).SendAsync("+91 98765 43210", "sms");

        code.Should().MatchRegex("^[0-9]{6}$");
        var query = handler.Last!.RequestUri!.Query;
        query.Should().Contain("message_id=41731");
        query.Should().Contain("phone_number_id=1420447271151765");
        query.Should().Contain("numbers=9876543210");
        query.Should().Contain($"variables_values={code}");
        query.Should().NotContain("|");
        handler.Last.RequestUri.AbsolutePath.Should().Be("/dev/whatsapp");
        handler.Last.RequestUri.AbsolutePath.Should().NotContain("otp/verify");
    }

    [Fact]
    public async Task Provider_rejection_does_not_return_a_code()
    {
        var handler = new StubHandler
        {
            Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, """{"return":false,"message":"Invalid Authentication"}""")),
        };

        var act = () => Sender(handler).SendAsync("9876543210", "sms");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(Fast2SmsWhatsAppOtpSender.DeliveryFailed);
    }

    [Fact]
    public async Task Http_200_without_a_success_flag_is_rejected()
    {
        var handler = new StubHandler
        {
            Respond = (_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"message":"queued"}""")),
        };

        var act = () => Sender(handler).SendAsync("9876543210", "sms");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Last.Should().NotBeNull();
    }

    [Fact]
    public async Task Timeout_does_not_return_a_code()
    {
        var handler = new StubHandler
        {
            Respond = (_, _) => throw new TaskCanceledException("timed out"),
        };

        var act = () => Sender(handler).SendAsync("9876543210", "sms");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(Fast2SmsWhatsAppOtpSender.DeliveryFailed);
    }

    [Fact]
    public async Task Network_failure_does_not_return_a_code()
    {
        var handler = new StubHandler
        {
            Respond = (_, _) => throw new HttpRequestException("down"),
        };

        var act = () => Sender(handler).SendAsync("9876543210", "sms");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Invalid_mobile_does_not_call_fast2sms()
    {
        var handler = new StubHandler();
        var act = () => Sender(handler).SendAsync("12345", "sms");

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Last.Should().BeNull();
    }

    [Theory]
    [InlineData(200, """{"return":true}""", true)]
    [InlineData(200, """{"return":false,"message":"Invalid OTP ID"}""", false)]
    [InlineData(200, """{"success":true}""", true)]
    [InlineData(200, """{"success":"false"}""", false)]
    [InlineData(401, """{"return":false}""", false)]
    [InlineData(500, """{"return":true}""", false)]
    [InlineData(200, "not-json", false)]
    [InlineData(200, "", false)]
    public void Acceptance_requires_the_provider_success_flag(int status, string body, bool accepted)
    {
        Fast2SmsWhatsAppResult.IsAccepted(status, body).Should().Be(accepted);
    }

    [Theory]
    [InlineData("+91 98765 43210", "9876543210")]
    [InlineData("919876543210", "9876543210")]
    [InlineData("09876543210", "9876543210")]
    [InlineData("9876543210", "9876543210")]
    [InlineData("5876543210", null)]
    [InlineData("", null)]
    public void Indian_mobile_keeps_ten_digits(string raw, string? expected)
    {
        IndianMobile.ToTenDigits(raw).Should().Be(expected);
    }

    [Fact]
    public void Login_code_expires_in_ten_minutes()
    {
        LoginOtp.ExpiryMinutes.Should().Be(10);
        LoginOtp.EmailResendWaitSeconds.Should().Be(60);
        LoginOtp.WhatsAppResendWaitSeconds.Should().Be(300);
        LoginOtp.Lifetime.Should().Be(TimeSpan.FromMinutes(10));
    }
}
