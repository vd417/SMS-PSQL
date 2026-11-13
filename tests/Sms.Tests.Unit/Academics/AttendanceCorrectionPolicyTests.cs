using FluentAssertions;
using Sms.Application.Services.Academics;
using Xunit;

namespace Sms.Tests.Unit.Academics;

public class AttendanceCorrectionPolicyTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Owner_is_warned_from_the_third_change_in_the_month(int saves, bool warns)
    {
        AttendanceCorrectionPolicy.WarnsOwner(saves).Should().Be(warns);
    }
}
