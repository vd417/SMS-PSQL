using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Unit.PgMigrator;

public class CliTests
{
    private const string Base = "/app";

    [Fact]
    public void Connection_argument_wins_over_the_environment()
    {
        var o = Cli.Parse(["migrate", "--connection", "Host=arg"], "Host=env", Base, out _);
        o!.ConnectionString.Should().Be("Host=arg");
    }

    [Fact]
    public void Falls_back_to_the_environment_variable()
    {
        var o = Cli.Parse(["status"], "Host=env", Base, out _);
        o!.ConnectionString.Should().Be("Host=env");
        o.Command.Should().Be("status");
    }

    [Fact]
    public void No_connection_anywhere_is_an_error()
    {
        Cli.Parse(["migrate"], null, Base, out var error).Should().BeNull();
        error.Should().Contain(Cli.ConnectionEnvVar);
    }

    [Fact]
    public void Defaults_directories_next_to_the_executable_and_a_60s_lock_timeout()
    {
        var o = Cli.Parse(["init"], "Host=env", Base, out _)!;
        o.BaselineDirectory.Should().Be(Path.Combine(Base, "baseline"));
        o.MigrationsDirectory.Should().Be(Path.Combine(Base, "migrations"));
        o.LockTimeout.Should().Be(TimeSpan.FromSeconds(60));
        o.BackupConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Parses_every_option()
    {
        var o = Cli.Parse(
            ["migrate", "--baseline-dir", "b", "--migrations-dir", "m", "--lock-timeout-seconds", "5", "--backup-confirmed"],
            "Host=env", Base, out _)!;
        o.BaselineDirectory.Should().Be("b");
        o.MigrationsDirectory.Should().Be("m");
        o.LockTimeout.Should().Be(TimeSpan.FromSeconds(5));
        o.BackupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("upgrade")]
    [InlineData("migrate --nope x")]
    [InlineData("migrate --connection")]
    [InlineData("migrate --lock-timeout-seconds 0")]
    [InlineData("migrate --lock-timeout-seconds abc")]
    public void Rejects_bad_arguments(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Cli.Parse(args, "Host=env", Base, out var error).Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
