using FluentAssertions;
using Sms.Shared.Kernel.Auth;

namespace Sms.Tests.Unit.Tenancy;

public class SchoolSmsRulesTests
{
    [Fact]
    public void Off_uses_the_platform_sender()
    {
        SchoolSmsRules.Resolve(false, "SCHMAT").Should().BeNull();
    }

    [Fact]
    public void On_without_a_sender_id_uses_the_platform_sender()
    {
        SchoolSmsRules.Resolve(true, "  ").Should().BeNull();
        SchoolSmsRules.Resolve(true, "ABC").Should().BeNull();
    }

    [Fact]
    public void On_with_a_sender_id_uses_the_school_sender()
    {
        SchoolSmsRules.Resolve(true, " schmat ").Should().Be("SCHMAT");
    }
}
