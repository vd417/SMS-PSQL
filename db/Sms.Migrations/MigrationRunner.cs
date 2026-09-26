using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace Sms.Migrations;

/// <remarks>
/// Hardcoded to <c>.AddSqlServer()</c> and can never run against the Postgres connection strings
/// used everywhere else in this codebase post-migration - <c>Sms.Api/Program.cs</c> no longer calls
/// this at all (see its startup comment); <c>db/postgres/*.sql</c> is the applied baseline schema
/// instead. Kept only because <c>db/Sms.Migrations/*.cs</c>'s 200+ embedded-SQL history remains a
/// useful historical reference when a live proc/table definition needs cross-checking (see
/// postgres_migration notes). Two tests that used to call <c>Run</c>/<c>RunTo</c> against a Postgres
/// connection string (<c>MigrationIdempotenceTests</c>, <c>M0085_EndToEnd_MigrationTests</c>) were
/// removed for exactly this reason - they could never pass, under either a "baseline + forward-only"
/// or a "rewrite every migration for Postgres" resolution of the still-open migration-history-strategy
/// decision, since both would require a real Postgres-capable processor here, which is a separate,
/// larger decision, not a test-only fix.
/// </remarks>
public static class MigrationRunner
{
    public static void Run(string connectionString)
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSqlServer()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>
    /// Migrates up to (and including) a specific version. Used by tests that need to insert
    /// data BETWEEN two migrations — e.g. inserting pre-existing rows after the schema
    /// migration but before the backfill migration runs, to prove the backfill's real
    /// data effect end-to-end rather than against hand-copied SQL snippets.
    /// </summary>
    public static void RunTo(string connectionString, long version)
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddSqlServer()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(MigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
    }
}
