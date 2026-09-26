using Dapper;
using Sms.Shared.Kernel.Data;

namespace Sms.Api.Extensions;

/// Refuses to run the API as a Postgres role that bypasses row-level security. A superuser (or a
/// BYPASSRLS role) silently ignores every tenant-isolation policy in db/postgres/07_rls_policies.sql,
/// even with FORCE ROW LEVEL SECURITY, so the app must connect as the non-superuser sms_app role
/// (db/postgres/00_app_role.sql). Outside Development this is fatal; in Development it logs an
/// error so a local user-secrets connection string pointing at "postgres" is loud, not silent.
/// An unreachable database is not this guard's concern and is left to the health checks.
public static class DatabaseRoleGuard
{
    public static async Task RunAsync(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseRoleGuard");

        (string Role, bool BypassesRls)? role;
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            await using var conn = await scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().OpenAsync();
            role = await conn.QuerySingleAsync<(string, bool)>(
                "SELECT rolname::text, (rolsuper OR rolbypassrls) FROM pg_roles WHERE rolname = current_user");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not check the database role at startup; RLS enforcement not verified.");
            return;
        }

        if (!role.Value.BypassesRls)
            return;

        const string message =
            "The API is connected to Postgres as role '{Role}', which bypasses row-level security, so " +
            "tenant isolation is NOT enforced. Connect as the non-superuser sms_app role instead.";
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException(message.Replace("{Role}", role.Value.Role));
        log.LogError(message, role.Value.Role);
    }
}
