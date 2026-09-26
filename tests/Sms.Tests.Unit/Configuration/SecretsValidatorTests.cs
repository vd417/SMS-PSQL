using FluentAssertions;
using Sms.Shared.Kernel.Configuration;
using Xunit;

namespace Sms.Tests.Unit.Configuration;

public class SecretsValidatorTests
{
    private const string ValidKey = "a-perfectly-fine-signing-key-32-bytes-min!!";
    private const string Conn = "Host=x;Database=y;Username=sms_app;Password=p";

    [Fact]
    public void Passes_with_valid_key_and_connection() =>
        FluentActions.Invoking(() => SecretsValidator.Validate(ValidKey, Conn)).Should().NotThrow();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_when_signing_key_missing(string? key) =>
        FluentActions.Invoking(() => SecretsValidator.Validate(key, Conn))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Throws_on_placeholder_key() =>
        FluentActions.Invoking(() => SecretsValidator.Validate(SecretsValidator.PlaceholderKey, Conn))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Throws_on_too_short_key() =>
        FluentActions.Invoking(() => SecretsValidator.Validate("short-key", Conn))
            .Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Throws_when_connection_string_missing(string? conn) =>
        FluentActions.Invoking(() => SecretsValidator.Validate(ValidKey, conn))
            .Should().Throw<InvalidOperationException>();
}
