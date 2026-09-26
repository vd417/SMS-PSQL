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
    // The old FluentMigrator SQL Server history (db/Sms.Migrations/M0001..M0210) no longer runs:
    // its embedded T-SQL has no direct Postgres equivalent. Schema provisioning is now
    // db/postgres/01_schemas.sql..08_sample_procedure_conversions.sql, applied once per database
    // (docker-compose's db service applies them automatically via /docker-entrypoint-initdb.d; for
    // a bare local Postgres, run them with psql in order). A Postgres-native forward-migration
    // mechanism to replace this runner for future schema changes is a separate, pending decision.
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
