using Npgsql;
using Sms.Shared.Kernel.Auth;

namespace Sms.DevSeed;

/// Usage: SMS_MIGRATOR_CONNECTION=<owner cs of a *_dev db> dotnet run --project tools/Sms.DevSeed -- --i-know-this-is-dev
/// Exit codes: 0 seeded (or already seeded), 1 refused, 2 failed (nothing committed).
public static class Cli
{
    public const string ConnectionEnvVar = "SMS_MIGRATOR_CONNECTION";

    public static async Task<int> Main(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        var error = DevSeedGuard.Check(cs, args);
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        try
        {
            var report = await SeedRunner.RunAsync(cs!, SeedData.Build(new PasswordHasher()));
            foreach (var table in SeedData.Tables)
                if (report.PerTable.TryGetValue(table, out var s))
                    Console.WriteLine($"{table,-22} inserted {s.Inserted,3}  skipped {s.Skipped,3}");
            Console.WriteLine($"Total inserted: {report.TotalInserted}");
            return 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Npgsql messages never contain the password; the connection string itself is never printed.
            Console.Error.WriteLine($"FAILED, nothing committed: {ex.Message}");
            return 2;
        }
    }
}
