namespace Sms.DevSeed;

/// One INSERT into "dbo"."<Table>". Values are never null: omit a column to take its default.
public sealed record SeedRow(string Table, IReadOnlyDictionary<string, object> Values);
