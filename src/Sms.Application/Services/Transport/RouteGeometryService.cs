using Sms.Modules.Transport;
using Sms.Shared.Kernel.Routing;

namespace Sms.Application.Services.Transport;

public enum RouteGeometryStatus { Available, Unavailable }

public sealed record RouteGeometryResult(
    Guid RouteId, RouteGeometryStatus Status, string? Format, string? Geometry,
    int? DistanceMeters, int? DurationSeconds, string StopSequenceHash, DateTime? GeneratedAt,
    string? Reason = null);

public interface IRouteGeometryService
{
    Task<RouteGeometryResult> GetAsync(Guid tenantId, Guid routeId, CancellationToken ct = default);
}

/// Orchestrates the planned-route geometry cache: reuse on hash match, regenerate via
/// Google Routes on a miss, and prefer stale cached geometry over "unavailable" whenever
/// the provider fails — this repo's product rule is "never show a fake straight line," not
/// "always show the freshest geometry."
///
/// Every planned route starts from the tenant's configured school location (the origin),
/// then follows the route's stops in Seq order. The school is the origin only — no
/// return-to-school leg is appended. When no school location is configured the geometry is
/// Unavailable with reason "school_location_not_configured"; the origin is never faked from
/// a stop or a GPS position.
public sealed class RouteGeometryService(
    IRouteStopSource stops, IRouteOriginSource origin, IGoogleRoutesClient routesClient, IRouteGeometryStore store) : IRouteGeometryService
{
    const string Format = "google-encoded-polyline";
    const string Provider = "google-routes";

    public async Task<RouteGeometryResult> GetAsync(Guid tenantId, Guid routeId, CancellationToken ct = default)
    {
        var orderedStops = await stops.ListRouteStopsAsync(routeId, ct);
        var school = await origin.GetSchoolOriginAsync(tenantId, ct);

        // School origin is mandatory — never substitute a stop or GPS as the start.
        if (school is null)
            return Unavailable(routeId, RouteGeometryHasher.Compute(orderedStops), "school_location_not_configured");

        var originPoint = (school.Lat, school.Lng);
        // School origin + at least one stop makes a drawable road route (>= 2 waypoints).
        if (orderedStops.Count < 1)
            return Unavailable(routeId, RouteGeometryHasher.Compute(orderedStops, originPoint), "insufficient_stops");

        var hash = RouteGeometryHasher.Compute(orderedStops, originPoint);
        var cached = await store.GetAsync(routeId, ct);
        if (cached is not null && cached.StopSequenceHash == hash)
            return Available(cached);

        var waypoints = new List<RouteWaypoint> { new(school.Lat, school.Lng) };
        waypoints.AddRange(orderedStops.Select(s => new RouteWaypoint(s.Lat, s.Lng)));
        var computed = await routesClient.ComputeRouteAsync(waypoints, ct);
        if (computed is null)
            return cached is not null ? Available(cached) : Unavailable(routeId, hash, "provider_unavailable");

        var generatedAt = DateTime.UtcNow;
        await store.UpsertAsync(tenantId, routeId, hash, Format, computed.EncodedPolyline,
            computed.DistanceMeters, computed.DurationSeconds, Provider, generatedAt, ct);

        return new RouteGeometryResult(
            routeId, RouteGeometryStatus.Available, Format, computed.EncodedPolyline,
            computed.DistanceMeters, computed.DurationSeconds, hash, generatedAt);
    }

    static RouteGeometryResult Available(RouteGeometryRow row) => new(
        row.RouteId, RouteGeometryStatus.Available, row.Format, row.EncodedPolyline,
        row.DistanceMeters, row.DurationSeconds, row.StopSequenceHash, row.GeneratedAt);

    static RouteGeometryResult Unavailable(Guid routeId, string hash, string reason) => new(
        routeId, RouteGeometryStatus.Unavailable, null, null, null, null, hash, null, reason);
}
