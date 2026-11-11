using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;

namespace Sms.Api.Middleware;

/// Touches Users.LastSeenAt for authenticated requests, throttled to avoid write-amplification
/// on every API call — only writes when the stored value is more than 60s stale (or null).
/// Registered AFTER TenantResolutionMiddleware (not before, despite running "regardless of
/// tenant/billing state"): IDbConnectionFactory.OpenAsync stamps SESSION_CONTEXT from
/// ITenantContext, which TenantResolutionMiddleware populates. Running earlier would open a
/// connection with no TenantId/IsPlatform in session context, and dbo.Users' RLS block
/// predicate would silently reject the UPDATE for every non-platform user.
public sealed class LastSeenTouchMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IDbConnectionFactory factory)
    {
        var sub = http.User.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var userId) && http.User.FindFirst("is_platform")?.Value != "1")
        {
            await using var conn = await factory.OpenAsync(http.RequestAborted);
            var status = await conn.QuerySingleOrDefaultAsync<string>(
                """SELECT "Status" FROM "dbo"."Users" WHERE "Id" = @userId""",
                new { userId });
            if (status is "inactive" or "removed")
            {
                var code = status == "inactive" ? AccountSuspension.Code : "access_removed";
                var message = status == "inactive"
                    ? AccountSuspension.Message
                    : "Your access to this school has been removed by the admin.";
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    error = new { code, message },
                }), http.RequestAborted);
                return;
            }
            await conn.ExecuteAsync(
                """
                UPDATE "dbo"."Users" SET "LastSeenAt" = now()
                WHERE "Id" = @userId AND ("LastSeenAt" IS NULL OR "LastSeenAt" < now() - interval '60 seconds')
                """,
                new { userId });
        }
        await next(http);
    }
}
