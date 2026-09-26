using FluentAssertions;
using Sms.DevSeed;
using Xunit;

namespace Sms.Tests.Unit.DevSeed;

public class DevSeedGuardTests
{
    private const string DevCs = "Host=localhost;Database=sms_dev;Username=owner";
    private static readonly string[] Confirmed = [DevSeedGuard.ConfirmFlag];

    [Fact]
    public void Allows_a_dev_database_with_the_confirm_flag() =>
        DevSeedGuard.Check(DevCs, Confirmed).Should().BeNull();

    [Fact]
    public void Refuses_without_the_confirm_flag() =>
        DevSeedGuard.Check(DevCs, []).Should().Contain(DevSeedGuard.ConfirmFlag);

    [Theory]
    [InlineData("Host=localhost;Database=sms;Username=owner")]
    [InlineData("Host=localhost;Database=sms_prod;Username=owner")]
    [InlineData("Host=localhost;Database=sms_dev_backup;Username=owner")]
    [InlineData("Host=localhost;Username=owner")]
    public void Refuses_any_database_not_ending_in_dev(string cs) =>
        DevSeedGuard.Check(cs, Confirmed).Should().StartWith("Refusing to seed database");

    [Fact]
    public void Refuses_when_the_connection_env_var_is_missing() =>
        DevSeedGuard.Check(null, Confirmed).Should().Contain("SMS_MIGRATOR_CONNECTION");

    [Fact]
    public void Refusal_never_echoes_the_connection_string()
    {
        const string secretish = "Host=localhost;Database=sms_prod;Username=owner;Password=hunter2";
        DevSeedGuard.Check(secretish, Confirmed).Should().NotContain("hunter2");
    }
}
