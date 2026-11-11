using FluentAssertions;
using Sms.Shared.Kernel.Auth;

namespace Sms.Tests.Unit.Tenancy;

public class SchoolMailFromRulesTests
{
    [Fact]
    public void Off_uses_the_platform_mailbox()
    {
        SchoolMailFromRules.Resolve(false, "Riverdale", "fees@school.edu", null).Should().BeNull();
    }

    [Fact]
    public void On_without_an_address_uses_the_platform_mailbox()
    {
        SchoolMailFromRules.Resolve(true, "Riverdale", "  ", "office@school.edu").Should().BeNull();
    }

    [Fact]
    public void On_with_an_address_uses_the_school_mailbox()
    {
        var from = SchoolMailFromRules.Resolve(true, " Riverdale ", "fees@school.edu", "office@school.edu");
        from.Should().NotBeNull();
        from!.Name.Should().Be("Riverdale");
        from.Address.Should().Be("fees@school.edu");
        from.ReplyTo.Should().Be("office@school.edu");
    }

    [Fact]
    public void Gmail_requires_the_from_address_to_match_the_mailbox()
    {
        SchoolMailFromRules.SenderAuthorized("gmail", "fees@school.edu", "other@school.edu").Should().BeFalse();
        SchoolMailFromRules.SenderAuthorized("workspace", "fees@school.edu", "fees@school.edu").Should().BeTrue();
        SchoolMailFromRules.SenderAuthorized("outlook", "fees@school.edu", "office@school.edu").Should().BeTrue();
    }

    [Fact]
    public void Templates_escape_values_and_drop_scripts()
    {
        var html = SchoolEmailTemplate.Render(
            "<script>alert(1)</script>Hello {{student_name}} <b onclick=\"x\">{{unknown}}</b>",
            new Dictionary<string, string> { ["student_name"] = "<Aarav>" });
        html.Should().NotContain("script");
        html.Should().NotContain("onclick");
        html.Should().Contain("&lt;Aarav&gt;");
        html.Should().NotContain("{{unknown}}");
    }

    [Fact]
    public void Authentication_failures_are_not_retried()
    {
        SmtpFailure.IsPermanent(new MailKit.Security.AuthenticationException("no")).Should().BeTrue();
        SmtpFailure.IsPermanent(new InvalidOperationException("network")).Should().BeFalse();
    }
}
