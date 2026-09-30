using Microsoft.Extensions.Logging;
using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Push;

namespace Sms.Application.Services.Transport;

public interface IBusParentAlertService
{
    Task NotifyTripStartedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default);
    Task NotifyApproachingStopsAsync(
        Guid tenantId, Guid busId, Guid tripId, double lat, double lng,
        double? accuracyMeters, DateTime? lastPingAtUtc, Guid? targetStopId, CancellationToken ct = default);
}

/// Fan-out in-app notices to each child's parent when a trip starts, and again when the bus
/// comes within 1 km of that child's assigned stop. Deduped per trip so GPS pings cannot spam.
public sealed class BusParentAlertService(
    StudentBusRepository riders,
    CommsRepository comms,
    DeviceTokenRepository devices,
    IExpoPushSender push,
    ILiveBroadcaster live,
    ILogger<BusParentAlertService> logger) : IBusParentAlertService
{
    public Task NotifyTripStartedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default) =>
        NotifyAsync(tenantId, busId, tripId, BusParentAlertRules.TripStarted,
            busLat: null, busLng: null, accuracyMeters: null, lastPingAtUtc: null, targetStopId: null, ct);

    public Task NotifyApproachingStopsAsync(
        Guid tenantId, Guid busId, Guid tripId, double lat, double lng,
        double? accuracyMeters, DateTime? lastPingAtUtc, Guid? targetStopId, CancellationToken ct = default) =>
        NotifyAsync(tenantId, busId, tripId, BusParentAlertRules.ApproachingStop,
            lat, lng, accuracyMeters, lastPingAtUtc, targetStopId, ct);

    private async Task NotifyAsync(
        Guid tenantId, Guid busId, Guid tripId, string kind, double? busLat, double? busLng,
        double? accuracyMeters, DateTime? lastPingAtUtc, Guid? targetStopId, CancellationToken ct)
    {
        try
        {
            // Never raise a "1 km away" notice off a stale or obviously unreliable fix.
            if (kind == BusParentAlertRules.ApproachingStop
                && (!BusParentAlertRules.IsFreshEnough(lastPingAtUtc, DateTime.UtcNow)
                    || !BusParentAlertRules.IsAccurateEnough(accuracyMeters)))
                return;

            var roster = await riders.ListRidersWithStopsAsync(busId, ct);
            var sent = false;
            foreach (var rider in roster)
            {
                if (kind == BusParentAlertRules.ApproachingStop)
                {
                    // Only the imminent stop's riders — respects trip stop sequence/direction
                    // (targetStopId = the trip's next-incomplete stop) so we never notify for a
                    // stop the bus already passed or one far ahead. When the target is unknown,
                    // fall back to distance-only (previous behaviour).
                    if (targetStopId is { } target && rider.StopId != target) continue;
                    if (busLat is not { } lat || busLng is not { } lng
                        || rider.StopLat is not { } stopLat || rider.StopLng is not { } stopLng)
                        continue;
                    var meters = TripRepository.Haversine(lat, lng, stopLat, stopLng);
                    if (!BusParentAlertRules.IsWithinApproach(meters)) continue;
                }

                var parents = await riders.ListParentUserIdsAsync(rider.StudentId, rider.AdmissionNo, ct);
                foreach (var parentId in parents)
                {
                    if (!await riders.TryInsertParentAlertAsync(tenantId, tripId, rider.StudentId, parentId, kind, ct))
                        continue;
                    var (title, body) = Copy(kind, rider);
                    await comms.CreateNotificationAsync(tenantId,
                        new CreateNotificationRequest("bus", "bus", title, body, parentId), ct);
                    // Best-effort device push alongside the in-app notice (never throws — a dead
                    // token or Expo outage must not break this fan-out or the trip flow).
                    var tokens = await devices.ListTokensForUserAsync(tenantId, parentId, ct);
                    if (tokens.Count > 0)
                        await push.SendAsync(tokens, title, body,
                            new Dictionary<string, object?> { ["kind"] = kind, ["trip_id"] = tripId }, ct);
                    sent = true;
                }
            }

            if (sent)
                await live.PublishAsync(tenantId, LiveEventTypes.Notification, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Parent bus alert {Kind} failed for trip {Trip}", kind, tripId);
        }
    }

    private static (string Title, string Body) Copy(string kind, BusRiderStopRow rider)
    {
        var bus = string.IsNullOrWhiteSpace(rider.BusNo) ? "the bus" : $"Bus #{rider.BusNo}";
        var child = string.IsNullOrWhiteSpace(rider.StudentName) ? "your child" : rider.StudentName;
        if (kind == BusParentAlertRules.ApproachingStop)
        {
            var stop = string.IsNullOrWhiteSpace(rider.StopName) ? "the stop" : rider.StopName;
            return ("Bus near stop", $"{bus} is about 1 km from {child}'s stop ({stop}).");
        }
        return ("Bus started", $"{bus} has started today's trip for {child}.");
    }
}
