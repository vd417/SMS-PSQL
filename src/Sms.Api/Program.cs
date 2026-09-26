using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Sms.Api.Endpoints.Auth;
using Sms.Api.Extensions;
using Sms.Shared.Kernel.Http;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

var builder = WebApplication.CreateBuilder(args);
builder.ConfigureSmsServices();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // Schema: db/postgres/*.sql is the immutable baseline for a brand-new database and
    // db/postgres/migrations/NNNN_*.sql are forward-only changes. Both are applied by db/Sms.PgMigrator
    // (init / migrate) before the API rolls out, never by the API itself, which connects as the
    // non-superuser sms_app role (see DatabaseRoleGuard). db/Sms.Migrations (FluentMigrator, SQL
    // Server) is historical reference only. See docs/runbooks/postgres-migrations.md.
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        foreach (var (key, title) in Sms.Api.Swagger.ApiAudienceMap.Apps)
            c.SwaggerEndpoint($"/swagger/{key}/swagger.json", title);
    });
}

await DatabaseRoleGuard.RunAsync(app);
await PlatformAdminSeeder.RunAsync(app);
await Sms.Api.Metrics.MetricsSnapshotWriter.RunAsync(app);

app.UseSerilogRequestLogging();
app.UseCors("sms");
// UseRateLimiter must run AFTER UseAuthentication: the "ai-search" policy partitions on
// http.User.FindFirst("sub") so each authenticated user gets their own budget, falling back to the
// remote IP only for unauthenticated callers. Before this middleware ran after authentication,
// HttpContext.User was always the unauthenticated principal here, so every request silently fell
// back to per-IP partitioning (an entire school behind one NAT gateway sharing one budget). The
// "auth" policy (used for login endpoints) partitions on IP unconditionally by design and is
// unaffected by this reordering — it never reads HttpContext.User.
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<Sms.Api.Middleware.LastSeenTouchMiddleware>();
app.UseMiddleware<BillingStateMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<Sms.Api.Hubs.LiveHub>("/hubs/live");
app.MapHub<Sms.Api.Hubs.TransportFleetHub>("/hubs/transport-fleet");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var status = report.Status == HealthStatus.Healthy ? "ready" : "unavailable";
        await ctx.Response.WriteAsJsonAsync(new { status });
    }
});

app.Run();

public partial class Program { }
