using FluentAssertions;
using Sms.Application.Services.Auth;
using Xunit;

namespace Sms.Tests.Unit.Auth;

public class ParentTenantAccessTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("trial")]
    [InlineData("past_due")]
    [InlineData(" Active ")]
    public void Allows_login_for_open_schools(string status) =>
        ParentTenantAccess.AllowsLogin(status).Should().BeTrue();

    [Theory]
    [InlineData("suspended")]
    [InlineData("deactivated")]
    [InlineData("cancelled")]
    [InlineData("hold")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_inactive_schools(string? status) =>
        ParentTenantAccess.AllowsLogin(status).Should().BeFalse();
}
