using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sms.Application.Services.Transport;

namespace Sms.Api.Controllers;

public sealed record RouteGeometryResponse(
    Guid RouteId, string Status, string? Format, string? Geometry,
    int? DistanceMeters, int? DurationSeconds, string StopSequenceHash, DateTime? GeneratedAt,
    string? Reason);

/// Canonical road-following route geometry, shared by every authorized consumer app
/// (CRM/admin, teacher, parent/student, driver/staff) — one endpoint, one contract.
/// Deliberately its own controller (not an action on TransportController) so it can carry
/// a broader [Authorize] than TransportController's class-level Principal-only policy;
/// see the per-route CanViewRouteAsync check below for the real access control.
[Route("v1/transport/routes")]
[Authorize]
public sealed class RouteGeometryController(
    IRouteGeometryService geometry, ITransportAuthorizationResolver authz) : ApiControllerBase
{
    [HttpGet("{routeId:guid}/geometry")]
    public async Task<IActionResult> GetGeometry(Guid routeId, CancellationToken ct)
    {
        var tenantIdRaw = User.FindFirst("tenant_id")?.Value;
        var userIdRaw = User.FindFirst("sub")?.Value;
        if (tenantIdRaw is null || userIdRaw is null || !Guid.TryParse(tenantIdRaw, out var tenantId) || !Guid.TryParse(userIdRaw, out var userId))
            return ForbiddenResult("Missing tenant/user context.");

        var roles = User.FindAll("role").Select(c => c.Value).ToArray();
        if (!await authz.CanViewRouteAsync(userId, tenantId, roles, routeId, ct))
            return ForbiddenResult("Not authorized to view this route.");

        var result = await geometry.GetAsync(tenantId, routeId, ct);
        return OkData(new RouteGeometryResponse(
            result.RouteId, result.Status == RouteGeometryStatus.Available ? "available" : "unavailable",
            result.Format, result.Geometry, result.DistanceMeters, result.DurationSeconds,
            result.StopSequenceHash, result.GeneratedAt, result.Reason));
    }
}
