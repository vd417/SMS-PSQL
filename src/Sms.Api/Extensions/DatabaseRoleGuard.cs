using Dapper;
using Sms.Shared.Kernel.Data;

namespace Sms.Api.Extensions;

/// Refuses to run the API as a Postgres role that bypasses row-level security. A superuser (or a
/// BYPASSRLS role) silently ignores every tenant-isolation policy in db/postgres/07_rls_policies.sql,
/// even with FORCE ROW LEVEL SECURITY, so the app must connect as the non-superuser sms_app role
/// (db/postgres/00_app_role.sql). Outside Development this is fatal, and so is being unable to run
/// the check at all (after a short retry for a database that is still starting): the process never
/// serves traffic with tenant isolation unverified. In Development both only log, so a local
/// user-secrets connection string pointing at "postgres", or a stopped local database, is loud but
/// doesn't block the developer.
public static class DatabaseRoleGuard
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseRoleGuard");
        var strict = !app.Environment.IsDevelopment();

        (string Role, bool BypassesRls)? role = null;
        for (var attempt = 1; role is null; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
                role = await conn.QuerySingleAsync<(string, bool)>(
                    "SELECT rolname::text, (rolsuper OR rolbypassrls) FROM pg_roles WHERE rolname = current_user");
            }
            catch (Exception ex)
            {
                if (!strict)
                {
                    log.LogWarning(ex, "Could not check the database role at startup; RLS enforcement not verified.");
                    return;
                }
                if (attempt >= Attempts)
                    throw new InvalidOperationException(
                        $"Could not verify the database role after {Attempts} attempts, so RLS enforcement is unverified; refusing to start.", ex);
                log.LogWarning(ex, "Database role check attempt {Attempt}/{Attempts} failed; retrying.", attempt, Attempts);
                await Task.Delay(RetryDelay);
            }
        }

        if (!role.Value.BypassesRls)
            return;

        const string message =
            "The API is connected to Postgres as role '{Role}', which bypasses row-level security, so " +
            "tenant isolation is NOT enforced. Connect as the non-superuser sms_app role instead.";
        if (strict)
            throw new InvalidOperationException(message.Replace("{Role}", role.Value.Role));
        log.LogError(message, role.Value.Role);
    }
}
