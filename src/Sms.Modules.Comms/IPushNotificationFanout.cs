namespace Sms.Modules.Comms;

/// Sends a phone or browser banner after an in-app notice is saved.
/// Implementations must return immediately; delivery happens off the request.
public interface IPushNotificationFanout
{
    void Enqueue(Guid tenantId, Guid? userId, string title, string? body);
}

public sealed class NoOpPushNotificationFanout : IPushNotificationFanout
{
    public void Enqueue(Guid tenantId, Guid? userId, string title, string? body) { }
}
