using Microsoft.Extensions.Logging;
using Sms.Application.Common;
using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;
using Sms.Modules.Sis.Data;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Application.Services.Transport;

public sealed record SendBusNotificationRequest(string? EventType, Guid? StopId, IReadOnlyList<string>? Channels);

public sealed record BusNotificationResponse(int Reach);

public interface IBusNotifyService
{
    Task<ApiResult<BusNotificationResponse>> NotifyAsync(
        Guid busId, SendBusNotificationRequest req, CancellationToken ct = default);
}

/// Admin-triggered "notify parents" from the Operations screen: an in-app notice (push) and/or
/// an SMS to the guardian of every child riding the bus, optionally narrowed to one stop.
/// Unlike <see cref="BusParentAlertService"/> this is a deliberate manual send, so it is not
/// deduped per trip.
public sealed class BusNotifyService(
    StudentBusRepository riders,
    StudentRepository students,
    CommsRepository comms,
    ISmsSender smsSender,
    ILiveBroadcaster live,
    ITenantContext tenant,
    ITenantFeatureSet features,
    ILogger<BusNotifyService> logger) : IBusNotifyService
{
    private static readonly HashSet<string> EventTypes = ["departed", "approaching", "arrived"];
    private static readonly HashSet<string> ChannelNames = ["push", "sms"];

    public async Task<ApiResult<BusNotificationResponse>> NotifyAsync(
        Guid busId, SendBusNotificationRequest req, CancellationToken ct = default)
    {
        if (!FeatureGate.Allowed(tenant, features, FeatureCatalog.Operations))
            return ApiResult<BusNotificationResponse>.Fail(new Error("feature_locked",
                $"This feature ({FeatureCatalog.Operations}) is not available on your plan."), 403);
        if (tenant.TenantId is not { } tid)
            return ApiResult<BusNotificationResponse>.Fail(new Error("forbidden", "no tenant context"), 403);

        var eventType = req.EventType?.Trim().ToLowerInvariant() ?? "";
        if (!EventTypes.Contains(eventType))
            return ApiResult<BusNotificationResponse>.Fail(
                new Error("invalid_event_type", "event_type must be departed, approaching or arrived"), 422);
        var channels = (req.Channels ?? []).Select(c => c.Trim().ToLowerInvariant()).ToHashSet();
        if (channels.Count == 0 || !channels.IsSubsetOf(ChannelNames))
            return ApiResult<BusNotificationResponse>.Fail(
                new Error("invalid_channels", "channels must be one or more of push, sms"), 422);
        if (!await riders.BusExistsAsync(busId, ct))
            return ApiResult<BusNotificationResponse>.Fail(new Error("not_found", "bus not found"), 404);

        var roster = (await riders.ListRidersWithStopsAsync(busId, ct))
            .Where(r => req.StopId is not { } stopId || r.StopId == stopId)
            .ToList();

        var pushed = new HashSet<Guid>();
        var texted = new HashSet<string>();
        foreach (var rider in roster)
        {
            var (title, body) = Copy(eventType, rider);

            if (channels.Contains("push"))
            {
                foreach (var parentId in await riders.ListParentUserIdsAsync(rider.StudentId, rider.AdmissionNo, ct))
                {
                    await comms.CreateNotificationAsync(tid, new CreateNotificationRequest("bus", "bus", title, body, parentId), ct);
                    pushed.Add(parentId);
                }
            }

            if (channels.Contains("sms"))
            {
                var phone = Digits((await students.GetAsync(rider.StudentId, ct))?.GuardianPhone);
                if (phone.Length >= 10 && texted.Add(phone + "|" + rider.StudentId))
                    await smsSender.SendAsync(phone, $"{title}: {body}", ct);
            }
        }

        if (pushed.Count > 0)
            await live.PublishAsync(tid, LiveEventTypes.Notification, ct: ct);
        logger.LogInformation("Bus {Bus} manual {Event} notice → push {Push}, sms {Sms}",
            busId, eventType, pushed.Count, texted.Count);

        return ApiResult<BusNotificationResponse>.Ok(new BusNotificationResponse(pushed.Count + texted.Count));
    }

    private static (string Title, string Body) Copy(string eventType, BusRiderStopRow rider)
    {
        var bus = string.IsNullOrWhiteSpace(rider.BusNo) ? "The bus" : $"Bus #{rider.BusNo}";
        var child = string.IsNullOrWhiteSpace(rider.StudentName) ? "your child" : rider.StudentName;
        var stop = string.IsNullOrWhiteSpace(rider.StopName) ? "the stop" : rider.StopName;
        return eventType switch
        {
            "approaching" => ("Bus approaching", $"{bus} is approaching {child}'s stop ({stop})."),
            "arrived" => ("Bus arrived", $"{bus} has arrived at {child}'s stop ({stop})."),
            _ => ("Bus departed", $"{bus} has departed on {child}'s route."),
        };
    }

    private static string Digits(string? phone) => new((phone ?? "").Where(char.IsDigit).ToArray());
}
