using FluentAssertions;
using Sms.Shared.Kernel.Auth;
using Xunit;

namespace Sms.Tests.Unit.Auth;

public class WelcomeOnboardEmailTests
{
    [Fact]
    public void Build_welcomes_with_school_name_and_login_link_but_no_otp()
    {
        var msg = WelcomeOnboardEmail.Build("admin@demo.edu", "Greenwood High", "https://panel.schoolmate.app/", "Owner");

        msg.To.Should().Be("admin@demo.edu");
        msg.Subject.Should().Be("Welcome to Greenwood High on SchoolMate");
        msg.Body.Should().Contain("Greenwood High");
        msg.Body.Should().Contain("as Owner");
        msg.Body.Should().Contain("Create a password");
        msg.Body.Should().Contain("https://panel.schoolmate.app/");
        // The whole point: the welcome email carries NO one-time code.
        msg.Body.Should().NotContain("one-time code:");
        msg.Body.Should().NotContain("expires in 10 minutes");
        msg.HtmlBody.Should().NotBeNull();
        msg.HtmlBody!.Should().Contain("Create a password");
    }

    [Fact]
    public void SmsBody_points_to_the_panel_without_a_code()
    {
        var sms = WelcomeOnboardEmail.SmsBody("Greenwood High", "https://panel.schoolmate.app/", "Owner");

        sms.Should().Contain("Greenwood High");
        sms.Should().Contain("https://panel.schoolmate.app/");
        sms.Should().Contain("Create a password");
        sms.Should().NotContain("code is");
    }
}
