using Sms.Modules.Transport;

namespace Sms.Application.Services.Transport;

/// <summary>Default fleet broadcaster when SignalR hub wiring is not configured.</summary>
internal sealed class NoOpTransportFleetBroadcaster : ITransportFleetBroadcaster
{
    public Task BroadcastFleetAsync(Guid tenantId, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastPositionAsync(Guid busId, BusLiveSnapshotResponse snapshot, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastTripStartedAsync(Guid busId, Guid tripId, Guid? driverId, Guid? conductorId, string direction, DateTime startedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastTripEndedAsync(Guid busId, Guid tripId, DateTime endedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastStatusChangedAsync(Guid busId, Guid tripId, string status, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastStopArrivedAsync(Guid busId, Guid tripId, Guid stopId, string stopName, DateTime confirmedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastStopCompletedAsync(Guid busId, Guid tripId, Guid stopId, Guid? nextStopId, string? nextStopName, DateTime departedAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastSchoolArrivedAsync(Guid busId, Guid tripId, DateTime arrivedAt, int studentsOnboard, CancellationToken ct = default) => Task.CompletedTask;
    public Task BroadcastBoardingAsync(Guid schoolId, Guid busId, Guid tripId, Guid studentId, string state, DateTime at, CancellationToken ct = default) => Task.CompletedTask;
}
