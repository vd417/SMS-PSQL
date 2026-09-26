using FluentAssertions;
using Sms.PgMigrator;

namespace Sms.Tests.Unit.PgMigrator;

public sealed class MigrationFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sms_migfile_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string name, string sql) => File.WriteAllText(Path.Combine(_dir, name), sql);

    [Fact]
    public void Loads_in_numeric_order_with_name_and_version()
    {
        Write("0010_later.sql", "SELECT 10;");
        Write("0002_second.sql", "SELECT 2;");
        Write("0001_first.sql", "SELECT 1;");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "not sql, ignored");

        var files = MigrationFile.LoadDirectory(_dir);

        files.Select(f => f.Version).Should().Equal(1, 2, 10);
        files[0].Name.Should().Be("first");
        files[0].FileName.Should().Be("0001_first.sql");
        files[0].Sql.Should().Be("SELECT 1;");
    }

    [Theory]
    [InlineData("1_short.sql")]
    [InlineData("0001-dash.sql")]
    [InlineData("0001_Upper.sql")]
    [InlineData("0000_zero.sql")]
    [InlineData("fix.sql")]
    public void Rejects_a_sql_file_with_a_bad_name(string name)
    {
        Write(name, "SELECT 1;");
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage($"*{name}*");
    }

    [Fact]
    public void Rejects_duplicate_versions()
    {
        Write("0001_a.sql", "SELECT 1;");
        Write("0001_b.sql", "SELECT 1;");
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage("*Duplicate migration version 0001*");
    }

    [Fact]
    public void Missing_directory_is_an_error()
    {
        var load = () => MigrationFile.LoadDirectory(Path.Combine(_dir, "nope"));
        load.Should().Throw<MigrationException>().WithMessage("*not found*");
    }

    [Fact]
    public void Checksum_ignores_line_ending_style()
    {
        MigrationFile.ComputeChecksum("SELECT 1;\r\nSELECT 2;\r\n")
            .Should().Be(MigrationFile.ComputeChecksum("SELECT 1;\nSELECT 2;\n"));
        MigrationFile.ComputeChecksum("SELECT 1;").Should().MatchRegex("^[0-9a-f]{64}$");
        MigrationFile.ComputeChecksum("SELECT 1;").Should().NotBe(MigrationFile.ComputeChecksum("SELECT 2;"));
    }

    [Fact]
    public void Destructive_directive_is_read_from_the_leading_comment_block_only()
    {
        Write("0001_drop.sql", "-- 0001: drops a column\n-- sms:destructive\n\nALTER TABLE t DROP COLUMN c;");
        Write("0002_safe.sql", "SELECT 1;\n-- sms:destructive\n");

        var files = MigrationFile.LoadDirectory(_dir);

        files[0].Destructive.Should().BeTrue();
        files[1].Destructive.Should().BeFalse();
    }

    [Theory]
    [InlineData("CREATE TABLE t (id int);\nCOMMIT;")]
    [InlineData("BEGIN;\nCREATE TABLE t (id int);")]
    [InlineData("rollback;")]
    [InlineData("START TRANSACTION;")]
    [InlineData("COMMIT WORK;")]
    public void Rejects_a_file_that_controls_its_own_transaction(string sql)
    {
        Write("0001_tx.sql", sql);
        var load = () => MigrationFile.LoadDirectory(_dir);
        load.Should().Throw<MigrationException>().WithMessage("*transaction*");
    }

    [Fact]
    public void Plpgsql_block_begin_and_end_are_allowed()
    {
        Write("0001_fn.sql", "DO $$\nBEGIN\n  PERFORM 1;\nEND;\n$$;");
        MigrationFile.LoadDirectory(_dir).Should().ContainSingle();
    }
}
