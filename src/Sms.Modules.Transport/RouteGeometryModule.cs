using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Transport;

public sealed record RouteGeometryRow(
    Guid RouteId, string StopSequenceHash, string Format, string EncodedPolyline,
    int DistanceMeters, int DurationSeconds, string Provider, DateTime GeneratedAt);

/// Seam interfaces so Sms.Application's RouteGeometryService can be unit-tested without a real DB
/// connection. Declared here (not in Sms.Application) because Sms.Modules.Transport must not
/// reference Sms.Application — Sms.Application already references Sms.Modules.Transport, so the
/// reverse reference would be circular. BusRepository/RouteGeometryRepository implement these
/// directly; no wrapper classes are needed.
public interface IRouteStopSource
{
    Task<IReadOnlyList<RouteStopListItem>> ListRouteStopsAsync(Guid routeId, CancellationToken ct = default);
}

public interface IRouteGeometryStore
{
    Task<RouteGeometryRow?> GetAsync(Guid routeId, CancellationToken ct = default);
    Task UpsertAsync(Guid tenantId, Guid routeId, string stopSequenceHash, string format, string encodedPolyline,
        int distanceMeters, int durationSeconds, string provider, DateTime generatedAt, CancellationToken ct = default);
}

public sealed class RouteGeometryRepository(IDbConnectionFactory factory) : BaseRepository(factory), IRouteGeometryStore
{
    public async Task<RouteGeometryRow?> GetAsync(Guid routeId, CancellationToken ct = default) =>
        (await QueryInlineAsync<RouteGeometryRow>(
            @"SELECT ""RouteId"", ""StopSequenceHash"", ""Format"", ""EncodedPolyline"", ""DistanceMeters"", ""DurationSeconds"", ""Provider"", ""GeneratedAt""
              FROM ""dbo"".""RouteGeometries"" WHERE ""RouteId"" = @routeId",
            new { routeId }, ct)).FirstOrDefault();

    public Task UpsertAsync(
        Guid tenantId, Guid routeId, string stopSequenceHash, string format, string encodedPolyline,
        int distanceMeters, int durationSeconds, string provider, DateTime generatedAt, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            @"INSERT INTO ""dbo"".""RouteGeometries""
                  (""RouteId"", ""TenantId"", ""StopSequenceHash"", ""Format"", ""EncodedPolyline"", ""DistanceMeters"", ""DurationSeconds"", ""Provider"", ""GeneratedAt"")
                  VALUES (@routeId, @tenantId, @stopSequenceHash, @format, @encodedPolyline, @distanceMeters, @durationSeconds, @provider, @generatedAt)
              ON CONFLICT (""RouteId"") DO UPDATE SET
                  ""StopSequenceHash"" = @stopSequenceHash, ""Format"" = @format, ""EncodedPolyline"" = @encodedPolyline,
                  ""DistanceMeters"" = @distanceMeters, ""DurationSeconds"" = @durationSeconds,
                  ""Provider"" = @provider, ""GeneratedAt"" = @generatedAt",
            new { routeId, tenantId, stopSequenceHash, format, encodedPolyline, distanceMeters, durationSeconds, provider, generatedAt },
            ct);
}

public static class RouteGeometryModuleExtensions
{
    public static IServiceCollection AddRouteGeometryModule(this IServiceCollection services) =>
        services.AddScoped<RouteGeometryRepository>();
}
