using FluentAssertions;
using Npgsql;
using Xunit;

namespace Sms.Tests.Integration.Contacts;

[Collection("sql")]
public class ContactNormalizeFnTests(PostgresFixture fx)
{
    private async Task<string?> CallAsync(string fn, string? input)
    {
        await using var c = new NpgsqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT dbo.{fn}($1)", c);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)input ?? DBNull.Value });
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? null : (string)r;
    }

    [Theory]
    [InlineData(" ABC@X.com ", "abc@x.com")]
    [InlineData("Abc@x.com", "abc@x.com")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task NormalizeEmail(string? input, string? expected) =>
        (await CallAsync("normalize_email", input)).Should().Be(expected);

    [Theory]
    [InlineData("+91 98765 43210", "9876543210")]
    [InlineData("919876543210", "9876543210")]
    [InlineData("09876543210", "9876543210")]
    [InlineData("9876543210", "9876543210")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("12345", "12345")]
    public async Task NormalizePhone(string? input, string? expected) =>
        (await CallAsync("normalize_phone", input)).Should().Be(expected);
}
