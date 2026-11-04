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

            if (r.TripId is null)
                status = BusTrackingStatusRules.LegacyIdle;
            else if (!gpsAllowed)
            {
                status = BusTrackingStatusRules.LegacyRunning;
                trackingStatus = BusTrackingStatusRules.Offline;
                lat = null;
                lng = null;
                speed = null;
                lastPing = null;
                heading = null;
            }
            else
            {
                // A stored 0,0 is not a location. Treat it as "no ping" so the trip stays RUNNING
                // and no client draws a pin in the ocean.
                var pingAt = IsZero(r.Lat, r.Lng) ? null : r.LastPingAt;
                var derived = BusTrackingStatusRules.Derive(
                    now, pingAt, r.SpeedKmh, hasLiveTrip: true, gpsAllowed: true);
                trackingStatus = derived.Tracking;
                status = derived.Legacy;
                if (pingAt is null)
                {
                    lat = null;
                    lng = null;
                    speed = null;
                    heading = null;
                    lastPing = null;
                }
                else if (derived.Tracking == BusTrackingStatusRules.Live)
                {
                    var position = await repo.GetPositionAsync(r.BusId, ct);
                    nextStop = position.NextStopName;
                    etaMinutes = position.EtaMinutes;
                }
            }

            var travelingTeachers = await repo.ListTravelingTeachersAsync(r.BusId, ct);

            list.Add(new FleetBusResponse(
                r.BusId, r.RouteId, r.BusNo, r.RouteName, r.Driver, r.DriverPhone,
                r.StopCount, r.StudentsRiding, status,
                lat, lng, speed, nextStop, lastPing,
                teacherRow?.TeacherUserId, teacherRow?.TeacherName, Capacity: r.Capacity,
                TrackingStatus: trackingStatus, Heading: heading, EtaMinutes: etaMinutes,
                TravelingTeachers: travelingTeachers));
        }

        return list;
    }

    private static bool IsZero(double? lat, double? lng) => lat is 0 && lng is 0;
}
