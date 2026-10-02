using Sms.Application.Common;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Application.Services.Transport;

/// Builds the live fleet board snapshot (shared by HTTP and SignalR).
public sealed class FleetSnapshotBuilder(BusRepository repo, ITenantContext tenant, ITenantFeatureSet features, IClock clock)
{
    public async Task<IReadOnlyList<FleetBusResponse>> BuildAsync(CancellationToken ct = default)
    {
        var rows = await repo.FleetAsync(ct);
        // A bus can carry more than one BusAssignments row (nothing enforces a single duty
        // teacher), which makes ListBusesAsync return it more than once; keep the first so the
        // fleet board never crashes on a duplicate key.
        var teachers = (await repo.ListBusesAsync(ct))
            .GroupBy(b => b.BusId)
            .ToDictionary(g => g.Key, g => g.First());
        var now = clock.UtcNow;
        var gpsAllowed = FeatureGate.Allowed(tenant, features, FeatureCatalog.TransportGps);
        var list = new List<FleetBusResponse>(rows.Count);

        foreach (var r in rows)
        {
            teachers.TryGetValue(r.BusId, out var teacherRow);
            string status;
            string? trackingStatus = BusTrackingStatusRules.Offline;
            string? nextStop = null;
            int? etaMinutes = null;
            double? lat = r.Lat;
            double? lng = r.Lng;
            double? speed = r.SpeedKmh;
            DateTime? lastPing = r.LastPingAt;
            double? heading = r.Heading;

            if (r.TripId is null || r.LastPingAt is null)
                status = "idle";
            else if (!gpsAllowed)
            {
                status = "idle";
                lat = null;
                lng = null;
                speed = null;
                lastPing = null;
                heading = null;
            }
            else
            {
                var derived = BusTrackingStatusRules.Derive(
                    now, r.LastPingAt, r.SpeedKmh, hasLiveTrip: true, gpsAllowed: true);
                trackingStatus = derived.Tracking;
                var ageMin = (now - r.LastPingAt.Value).TotalMinutes;
                status = ageMin > 5 ? "delayed"
                    : (r.SpeedKmh is <= 3) ? "at_stop"
                    : "on_route";
                var position = await repo.GetPositionAsync(r.BusId, ct);
                nextStop = position.NextStopName;
                etaMinutes = position.EtaMinutes;
            }

            list.Add(new FleetBusResponse(
                r.BusId, r.RouteId, r.BusNo, r.RouteName, r.Driver, r.DriverPhone,
                r.StopCount, r.StudentsRiding, status,
                lat, lng, speed, nextStop, lastPing,
                teacherRow?.TeacherUserId, teacherRow?.TeacherName, Capacity: r.Capacity,
                TrackingStatus: trackingStatus, Heading: heading, EtaMinutes: etaMinutes));
        }

        return list;
    }
}
