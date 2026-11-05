using Sms.Application.Services.Realtime;
using Sms.Modules.Comms;

namespace Sms.Application.Services.Comms;

/// <summary>
/// One live <c>notification</c> event per saved row. <c>UserId</c> null means the whole school;
/// a set id means only that person should toast. SignalR serializes this as snake_case.
/// </summary>
public static class NotificationEvents
{
    public static Task PublishAsync(
        ILiveBroadcaster live, Guid tenantId, NotificationResponse notice, Guid? userId, CancellationToken ct) =>
        live.PublishAsync(tenantId, LiveEventTypes.Notification, new NotificationLiveData(
            notice.Id, userId, notice.Title, notice.Body, notice.Icon, notice.Tone, notice.Unread, notice.Time), ct);

    public sealed record NotificationLiveData(
        Guid Id, Guid? UserId, string Title, string? Body, string? Icon, string? Tone, bool Unread, string? Time);
}
