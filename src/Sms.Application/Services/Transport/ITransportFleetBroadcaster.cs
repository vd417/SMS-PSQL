using Sms.Modules.Transport;

namespace Sms.Application.Services.Transport;

public interface ITransportFleetBroadcaster
{
    Task BroadcastFleetAsync(Guid tenantId, CancellationToken ct = default);
    Task BroadcastPositionAsync(Guid busId, BusLiveSnapshotResponse snapshot, CancellationToken ct = default);
    Task BroadcastTripStartedAsync(Guid busId, Guid tripId, Guid? driverId, Guid? conductorId, string direction, DateTime startedAt, CancellationToken ct = default);
    Task BroadcastTripEndedAsync(Guid busId, Guid tripId, DateTime endedAt, CancellationToken ct = default);
    Task BroadcastStatusChangedAsync(Guid busId, Guid tripId, string status, CancellationToken ct = default);
    Task BroadcastStopArrivedAsync(Guid busId, Guid tripId, Guid stopId, string stopName, DateTime confirmedAt, CancellationToken ct = default);
    Task BroadcastStopCompletedAsync(Guid busId, Guid tripId, Guid stopId, Guid? nextStopId, string? nextStopName, DateTime departedAt, CancellationToken ct = default);
    Task BroadcastSchoolArrivedAsync(Guid busId, Guid tripId, DateTime arrivedAt, int studentsOnboard, CancellationToken ct = default);
    /// Pickup/drop (and absent) after the boarding row is saved. Bus group only — same authorization as other trip events.
    Task BroadcastBoardingAsync(Guid schoolId, Guid busId, Guid tripId, Guid studentId, string state, DateTime at, CancellationToken ct = default);
}
