using Microsoft.AspNetCore.SignalR;
using Sms.Api.Hubs;
using Sms.Application.Services.Transport;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Api.Services;

public sealed class TransportFleetBroadcaster(
    IHubContext<TransportFleetHub> hub,
    FleetSnapshotBuilder fleet,
    ITenantContext tenant) : ITransportFleetBroadcaster
{
    public async Task BroadcastFleetAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenant.TenantId != tenantId) return;
        var snapshot = await fleet.BuildAsync(ct);
        await hub.Clients.Group(TransportFleetHub.TenantGroup(tenantId.ToString()))
            .SendAsync("fleet_update", snapshot, ct);
    }

    public async Task BroadcastPositionAsync(Guid busId, BusLiveSnapshotResponse snapshot, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("position_update", snapshot, ct);

    public async Task BroadcastTripStartedAsync(Guid busId, Guid tripId, Guid? driverId, Guid? conductorId, string direction, DateTime startedAt, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("trip_started",
            new { busId, tripId, driverId, conductorId, direction, startedAt }, ct);

    public async Task BroadcastTripEndedAsync(Guid busId, Guid tripId, DateTime endedAt, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("trip_ended",
            new { busId, tripId, endedAt }, ct);

    public async Task BroadcastStatusChangedAsync(Guid busId, Guid tripId, string status, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("status_changed",
            new { busId, tripId, status }, ct);

    public async Task BroadcastStopArrivedAsync(Guid busId, Guid tripId, Guid stopId, string stopName, DateTime confirmedAt, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("stop_arrived",
            new { busId, tripId, stopId, stopName, confirmedAt }, ct);

    public async Task BroadcastStopCompletedAsync(Guid busId, Guid tripId, Guid stopId, Guid? nextStopId, string? nextStopName, DateTime departedAt, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("stop_completed",
            new { busId, tripId, stopId, departedAt, nextStopId, nextStopName }, ct);

    public async Task BroadcastSchoolArrivedAsync(Guid busId, Guid tripId, DateTime arrivedAt, int studentsOnboard, CancellationToken ct = default) =>
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync("school_arrived",
            new { busId, tripId, arrivedAt, studentsOnboard }, ct);

    public async Task BroadcastBoardingAsync(Guid schoolId, Guid busId, Guid tripId, Guid studentId, string state, DateTime at, CancellationToken ct = default)
    {
        var name = state == "dropped" ? "student_drop" : "student_pickup";
        await hub.Clients.Group(TransportFleetHub.BusGroup(busId)).SendAsync(name,
            new { schoolId, busId, tripId, studentId, status = state, at }, ct);
    }
}
