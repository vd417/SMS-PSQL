using Dapper;
using Microsoft.AspNetCore.Http;
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
        if (Guid.TryParse(sub, out var userId))
        {
            await using var conn = await factory.OpenAsync(http.RequestAborted);
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
