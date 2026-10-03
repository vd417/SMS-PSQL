using Microsoft.Extensions.Logging;
using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Push;

namespace Sms.Application.Services.Transport;

public interface IBusTeacherAlertService
{
    Task NotifyTripStartedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default);
    Task NotifyTripEndedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default);
    Task NotifyApproachingStopsAsync(
        Guid tenantId, Guid busId, Guid tripId, double lat, double lng,
        double? accuracyMeters, DateTime? lastPingAtUtc, Guid? targetStopId, CancellationToken ct = default);
}

/// Fan-out in-app notices + best-effort push to a bus's teachers: the duty teacher and every
/// traveling teacher get trip-started and trip-ended; traveling teachers with a mapped stop also
/// get a "~1 km away" alert when the bus nears that stop. Deduped per trip/teacher/kind so GPS
/// pings cannot spam. Mirrors <see cref="BusParentAlertService"/>; teacher-worded copy; push is
/// best-effort (a dead token or Expo outage never breaks the trip/ping flow).
public sealed class BusTeacherAlertService(
    BusRepository buses,
    CommsRepository comms,
    DeviceTokenRepository devices,
    IExpoPushSender push,
    ILiveBroadcaster live,
    ILogger<BusTeacherAlertService> logger) : IBusTeacherAlertService
{
    public Task NotifyTripStartedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default) =>
        NotifyLifecycleAsync(tenantId, busId, tripId, BusParentAlertRules.TripStarted, ct);

    public Task NotifyTripEndedAsync(Guid tenantId, Guid busId, Guid tripId, CancellationToken ct = default) =>
        NotifyLifecycleAsync(tenantId, busId, tripId, BusParentAlertRules.TripEnded, ct);

    private async Task NotifyLifecycleAsync(Guid tenantId, Guid busId, Guid tripId, string kind, CancellationToken ct)
    {
        try
        {
            var sent = false;
            foreach (var t in await buses.ListBusTeachersAsync(busId, ct))
            {
                if (!await buses.TryInsertTeacherAlertAsync(tenantId, tripId, t.TeacherUserId, kind, ct)) continue;
                var (title, body) = Copy(kind, t.BusNo, null);
                await DeliverAsync(tenantId, tripId, t.TeacherUserId, kind, title, body, ct);
                sent = true;
            }
            if (sent) await live.PublishAsync(tenantId, LiveEventTypes.Notification, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teacher bus alert {Kind} failed for trip {Trip}", kind, tripId);
        }
    }

    public async Task NotifyApproachingStopsAsync(
        Guid tenantId, Guid busId, Guid tripId, double lat, double lng,
        double? accuracyMeters, DateTime? lastPingAtUtc, Guid? targetStopId, CancellationToken ct = default)
    {
        try
        {
            // Never raise a "1 km away" notice off a stale or unreliable fix (same gate as parents).
            if (!BusParentAlertRules.IsFreshEnough(lastPingAtUtc, DateTime.UtcNow)
                || !BusParentAlertRules.IsAccurateEnough(accuracyMeters))
                return;

            var sent = false;
            foreach (var t in await buses.ListStopMappedTeachersAsync(busId, ct))
            {
                // Only the imminent stop (the trip's next-incomplete stop) when known; else distance-only.
                if (targetStopId is { } target && t.StopId != target) continue;
                if (t.StopLat is not { } stopLat || t.StopLng is not { } stopLng) continue;
                if (!BusParentAlertRules.IsWithinApproach(TripRepository.Haversine(lat, lng, stopLat, stopLng))) continue;
                if (!await buses.TryInsertTeacherAlertAsync(tenantId, tripId, t.TeacherUserId, BusParentAlertRules.ApproachingStop, ct))
                    continue;
                var (title, body) = Copy(BusParentAlertRules.ApproachingStop, t.BusNo, t.StopName);
                await DeliverAsync(tenantId, tripId, t.TeacherUserId, BusParentAlertRules.ApproachingStop, title, body, ct);
                sent = true;
            }
            if (sent) await live.PublishAsync(tenantId, LiveEventTypes.Notification, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Teacher bus approach alert failed for trip {Trip}", tripId);
        }
    }

    private async Task DeliverAsync(
        Guid tenantId, Guid tripId, Guid teacherUserId, string kind, string title, string body, CancellationToken ct)
    {
        await comms.CreateNotificationAsync(tenantId,
            new CreateNotificationRequest("bus", "bus", title, body, teacherUserId), ct);
        var tokens = await devices.ListTokensForUserAsync(tenantId, teacherUserId, ct);
        if (tokens.Count > 0)
            await push.SendAsync(tokens, title, body,
                new Dictionary<string, object?> { ["kind"] = kind, ["trip_id"] = tripId }, ct);
    }

    private static (string Title, string Body) Copy(string kind, string? busNo, string? stopName)
    {
        var bus = string.IsNullOrWhiteSpace(busNo) ? "The bus" : $"Bus {busNo}";
        if (kind == BusParentAlertRules.ApproachingStop)
        {
            var stop = string.IsNullOrWhiteSpace(stopName) ? "your stop" : stopName;
            return ("Bus near your stop", $"{bus} is about 1 km from {stop}.");
        }
        if (kind == BusParentAlertRules.TripEnded)
            return ("Bus trip ended", $"{bus} has ended its trip.");
        return ("Bus started", $"{bus} has started its trip.");
    }
}
