using Microsoft.Extensions.Configuration;
using Sms.Application.Common;
using Sms.Application.Services.Realtime;
using Sms.Modules.Transport;
using Sms.Shared.Kernel.Results;
using Sms.Shared.Kernel.Tenancy;
using Sms.Shared.Kernel.Time;

namespace Sms.Application.Services.Transport;

public interface ITripService
{
    Task<ApiResult<TripResponse>> StartAsync(StartTripRequest req, CancellationToken ct = default);
    /// Admin/CRM operator path: does not require the caller to be the bus's assigned driver
    /// or conductor. DriverId is the bus's assigned driver when one exists; when the bus has
    /// no assigned driver, DriverId falls back to the calling operator's own user id
    /// (pre-existing behaviour, preserved on purpose for buses with no staff assignment yet).
    Task<ApiResult<TripResponse>> StartAsOperatorAsync(StartTripRequest req, CancellationToken ct = default);
    Task<ApiResult<TripResponse?>> GetCurrentAsync(CancellationToken ct = default);
    Task<ApiResult<StaffTripAssignmentResponse>> GetAssignmentAsync(CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<StaffRosterStudentResponse>>> GetRosterAsync(Guid tripId, CancellationToken ct = default);
    Task<ApiResult> IngestPingsAsync(Guid tripId, BulkPingRequest req, CancellationToken ct = default);
    /// Admin/CRM operator path: same broadcast + heartbeat as driver ingest, without trip-participant check.
    Task<ApiResult> IngestOperatorPingsAsync(Guid tripId, BulkPingRequest req, CancellationToken ct = default);
    Task<ApiResult<TripSummaryResponse>> EndAsync(Guid tripId, CancellationToken ct = default);
    /// Admin/CRM operator path: end trip + trip_ended broadcast without participant check.
    Task<ApiResult<TripSummaryResponse>> EndAsOperatorAsync(Guid tripId, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<BoardingResponse>>> ListBoardingAsync(Guid tripId, CancellationToken ct = default);
    Task<ApiResult> UpsertBoardingAsync(Guid tripId, BoardingRequest req, CancellationToken ct = default);
    Task<ApiResult> ConfirmStopArrivalAsync(Guid tripId, Guid stopId, CancellationToken ct = default);
    Task<ApiResult> CompleteStopAsync(Guid tripId, Guid stopId, CancellationToken ct = default);
    Task<ApiResult> MarkSchoolArrivedAsync(Guid tripId, CancellationToken ct = default);
    Task<ApiResult<TripStopsResponse>> GetStopProgressAsync(Guid tripId, CancellationToken ct = default);
}

/// Every mutation that changes a trip's live state (start/ping/end) also pushes a fleet snapshot
/// and a live event, matching the bus-duty lifecycle in BusService — otherwise a driver-started
/// trip would only ever be visible to pollers, defeating the point of "live" tracking.
public sealed class TripService(
    TripRepository repo, BusRepository buses, ITenantContext tenant,
    ITransportFleetBroadcaster fleetBroadcaster, ILiveBroadcaster live, IClock clock,
    IConfiguration config, IBusParentAlertService parentAlerts) : ITripService
{
    // Matches TransportOfflineSweepWorker's Math.Clamp-on-read convention for a config value
    // with a sane default and hard bounds, rather than trusting an unbounded/negative config
    // value straight through into the arrival check.
    private readonly double _arrivalRadiusMeters =
        Math.Clamp(config.GetValue<double?>("TransportStops:ArrivalRadiusMeters") ?? 100, 5, 1000);

    public async Task<ApiResult<TripResponse>> StartAsync(StartTripRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<TripResponse>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (string.IsNullOrWhiteSpace(req.BusNo))
            return ApiResult<TripResponse>.Fail(new Error("bus_no_required", "bus_no is required"), 422);
        // The bus assignment — never the caller — decides who the trip's driver and conductor are.
        // The caller only has to be one of them; a conductor pressing Start must not become DriverId.
        if (await repo.GetBusAssignmentByNoAsync(tid, req.BusNo, ct) is not { } bus)
            return ApiResult<TripResponse>.Fail(new Error("bus_not_found", "no bus with that number in this school"), 404);
        if (bus.DriverUserId is not { } driverUserId)
            return ApiResult<TripResponse>.Fail(new Error("no_driver_assigned", "this bus has no driver assigned"), 422);
        if (uid != driverUserId && uid != bus.ConductorUserId)
            return ApiResult<TripResponse>.Fail(new Error("not_assigned", "you are not assigned to this bus"), 403);

        var effective = req with { RouteId = req.RouteId ?? bus.RouteId };
        return await StartCoreAsync(tid, driverUserId, effective, ct);
    }

    public async Task<ApiResult<TripResponse>> StartAsOperatorAsync(StartTripRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<TripResponse>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (string.IsNullOrWhiteSpace(req.BusNo))
            return ApiResult<TripResponse>.Fail(new Error("bus_no_required", "bus_no is required"), 422);
        if (await repo.GetBusAssignmentByNoAsync(tid, req.BusNo, ct) is not { } bus)
            return ApiResult<TripResponse>.Fail(new Error("bus_not_found", "no bus with that number in this school"), 404);
        // No assignment check here — the operator path is trusted (admin/CRM), unlike the
        // staff-facing StartAsync. DriverId is the bus's assigned driver when there is one;
        // a bus with no driver assigned yet falls back to the calling operator's own user id,
        // preserving this endpoint's pre-existing behaviour rather than rejecting it outright.
        var driverUserId = bus.DriverUserId ?? uid;
        var effective = req with { RouteId = req.RouteId ?? bus.RouteId };
        return await StartCoreAsync(tid, driverUserId, effective, ct);
    }

    private async Task<ApiResult<TripResponse>> StartCoreAsync(
        Guid tid, Guid driverUserId, StartTripRequest req, CancellationToken ct)
    {
        // dbo.Trip_Start returns no row (instead of inserting) when the bus already has a live
        // trip; it fills ConductorId from Buses.ConductorStaffId itself.
        if (await repo.StartAsync(tid, driverUserId, req, ct) is not { } trip)
            return ApiResult<TripResponse>.Fail(new Error("bus_already_active", "This bus already has an active trip"), 409);
        await fleetBroadcaster.BroadcastFleetAsync(tid, ct);
        await live.PublishAsync(tid, LiveEventTypes.Transport, ct: ct);
        if (await repo.GetBusIdAsync(trip.Id, ct) is { } busId)
        {
            await fleetBroadcaster.BroadcastTripStartedAsync(busId, trip.Id, trip.DriverId, trip.ConductorId, trip.Direction, trip.StartedAt ?? clock.UtcNow, ct);
            await parentAlerts.NotifyTripStartedAsync(tid, busId, trip.Id, ct);
        }
        return ApiResult<TripResponse>.Ok(WithActiveBroadcaster(trip), 201);
    }

    public async Task<ApiResult<TripResponse?>> GetCurrentAsync(CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<TripResponse?>.Fail(new Error("forbidden", "no user context"), 403);
        var trip = await repo.GetCurrentAsync(uid, ct);
        if (trip is null) return ApiResult<TripResponse?>.Ok(null);
        var currentStopId = await repo.GetCurrentStopIdAsync(trip.Id, ct);
        return ApiResult<TripResponse?>.Ok(WithActiveBroadcaster(trip) with { CurrentStopId = currentStopId });
    }

    public async Task<ApiResult<TripStopsResponse>> GetStopProgressAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<TripStopsResponse>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult<TripStopsResponse>.Fail(new Error("forbidden", "not your trip"), 403);
        return await repo.GetStopProgressAsync(tid, tripId, ct) is { } progress
            ? ApiResult<TripStopsResponse>.Ok(progress)
            : ApiResult<TripStopsResponse>.Fail(new Error("not_found", "trip not found"), 404);
    }

    public async Task<ApiResult> IngestPingsAsync(Guid tripId, BulkPingRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);
        var role = await repo.GetParticipantRoleAsync(tid, tripId, uid, ct);
        if (role is null)
            return ApiResult.Fail(new Error("forbidden", "not your trip"), 403);
        if (!await repo.IsActiveAsync(tripId, ct))
            return ApiResult.Fail(new Error("trip_ended", "this trip has already ended"), 409);
        // Live driver/conductor path: eligible to raise parent "bus near stop" alerts.
        return await IngestPingsCoreAsync(tid, tripId, req, heartbeatRole: role, allowParentAlerts: true, ct);
    }

    public async Task<ApiResult> IngestOperatorPingsAsync(Guid tripId, BulkPingRequest req, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult.Fail(new Error("forbidden", "no tenant context"), 403);
        // Heartbeat column: treat CRM/admin ingest as driver ping so offline sweep stays correct.
        // But operator/admin ingest may be backfilled or replayed, so it must NOT raise parent alerts.
        return await IngestPingsCoreAsync(tid, tripId, req, heartbeatRole: "driver", allowParentAlerts: false, ct);
    }

    private async Task<ApiResult> IngestPingsCoreAsync(
        Guid tid, Guid tripId, BulkPingRequest req, string heartbeatRole, bool allowParentAlerts, CancellationToken ct)
    {
        await repo.IngestPingsAsync(tid, tripId, req.Pings, ct);
        await repo.MarkPingAsync(tripId, heartbeatRole, ct);
        await fleetBroadcaster.BroadcastFleetAsync(tid, ct);
        await live.PublishAsync(tid, LiveEventTypes.Transport, ct: ct);
        if (await repo.GetBusIdAsync(tripId, ct) is { } busId)
        {
            var snapshot = await buses.GetLiveSnapshotAsync(busId, ct);
            var currentStopId = await repo.GetCurrentStopIdAsync(tripId, ct);
            (double Lat, double Lng, Guid StopId)? approach = null;
            // Only probe for a next stop when not already sitting at a confirmed one —
            // arrival detection targets the NEXT stop, not the current one (see
            // TripStopRepositoryTests' note that excluding the current stop is the caller's job).
            if (currentStopId is null && await repo.GetTripRouteIdAsync(tripId, ct) is { } routeId
                && await repo.GetNextIncompleteStopAsync(tripId, routeId, ct) is { } nextStop
                && snapshot.Lat is { } lat && snapshot.Lng is { } lng)
            {
                var distance = TripRepository.Haversine(lat, lng, nextStop.Lat, nextStop.Lng);
                var withinRadius = StopArrivalRules.IsWithinRadius(distance, _arrivalRadiusMeters);
                snapshot = snapshot with { NextStopId = nextStop.Id, WithinArrivalRadius = withinRadius, CurrentStopId = currentStopId };
                approach = (lat, lng, nextStop.Id);
            }
            else
            {
                snapshot = snapshot with { CurrentStopId = currentStopId };
            }
            await fleetBroadcaster.BroadcastPositionAsync(busId, snapshot, ct);
            // Parent "bus near stop" alert: live driver/conductor path only, the next incomplete
            // stop only, and only off a fresh/accurate fix (staleness/accuracy gated in the service).
            if (allowParentAlerts && approach is { } ap)
                await parentAlerts.NotifyApproachingStopsAsync(
                    tid, busId, tripId, ap.Lat, ap.Lng, snapshot.Accuracy, snapshot.LastUpdateAt, ap.StopId, ct);
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult<TripSummaryResponse>> EndAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<TripSummaryResponse>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult<TripSummaryResponse>.Fail(new Error("forbidden", "not your trip"), 403);
        return await EndCoreAsync(tid, tripId, ct);
    }

    public async Task<ApiResult<TripSummaryResponse>> EndAsOperatorAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid)
            return ApiResult<TripSummaryResponse>.Fail(new Error("forbidden", "no tenant context"), 403);
        return await EndCoreAsync(tid, tripId, ct);
    }

    private async Task<ApiResult<TripSummaryResponse>> EndCoreAsync(Guid tid, Guid tripId, CancellationToken ct)
    {
        var busId = await repo.GetBusIdAsync(tripId, ct);
        var summary = await repo.EndAsync(tid, tripId, ct);
        await fleetBroadcaster.BroadcastFleetAsync(tid, ct);
        await live.PublishAsync(tid, LiveEventTypes.Transport, ct: ct);
        if (busId is { } bid)
            await fleetBroadcaster.BroadcastTripEndedAsync(bid, tripId, clock.UtcNow, ct);
        return ApiResult<TripSummaryResponse>.Ok(summary);
    }

    public async Task<ApiResult<StaffTripAssignmentResponse>> GetAssignmentAsync(CancellationToken ct = default)
    {
        if (tenant.UserId is not { } uid)
            return ApiResult<StaffTripAssignmentResponse>.Fail(new Error("forbidden", "no user context"), 403);
        var assignment = await repo.GetAssignmentAsync(uid, ct);
        return assignment is null
            ? ApiResult<StaffTripAssignmentResponse>.Fail(new Error("not_found", "no assigned bus"), 404)
            : ApiResult<StaffTripAssignmentResponse>.Ok(assignment);
    }

    public async Task<ApiResult<IReadOnlyList<StaffRosterStudentResponse>>> GetRosterAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<IReadOnlyList<StaffRosterStudentResponse>>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult<IReadOnlyList<StaffRosterStudentResponse>>.Fail(new Error("forbidden", "not your trip"), 403);
        return ApiResult<IReadOnlyList<StaffRosterStudentResponse>>.Ok(await repo.GetRosterAsync(tripId, ct));
    }

    public async Task<ApiResult<IReadOnlyList<BoardingResponse>>> ListBoardingAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult<IReadOnlyList<BoardingResponse>>.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult<IReadOnlyList<BoardingResponse>>.Fail(new Error("forbidden", "not your trip"), 403);
        return ApiResult<IReadOnlyList<BoardingResponse>>.Ok(await repo.ListBoardingAsync(tripId, ct));
    }

    private static readonly string[] ValidBoardingStates = ["boarded", "absent", "dropped"];

    public async Task<ApiResult> UpsertBoardingAsync(Guid tripId, BoardingRequest req, CancellationToken ct = default)
    {
        if (!ValidBoardingStates.Contains(req.State))
            return ApiResult.Fail(new Error("invalid_state", $"State must be one of: {string.Join(", ", ValidBoardingStates)}"), 400);
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult.Fail(new Error("forbidden", "not your trip"), 403);
        if (!await repo.IsActiveAsync(tripId, ct))
            return ApiResult.Fail(new Error("trip_ended", "this trip has already ended"), 409);
        await repo.UpsertBoardingAsync(tid, tripId, req, ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> ConfirmStopArrivalAsync(Guid tripId, Guid stopId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult.Fail(new Error("forbidden", "not your trip"), 403);
        if (!await repo.IsActiveAsync(tripId, ct))
            return ApiResult.Fail(new Error("trip_ended", "this trip has already ended"), 409);
        var currentStopId = await repo.GetCurrentStopIdAsync(tripId, ct);
        // Re-confirming the stop that's already current would otherwise silently re-run the
        // MERGE below and reset ConfirmedAt — reject it explicitly instead.
        if (currentStopId == stopId)
            return ApiResult.Fail(new Error("already_at_stop", "this stop is already confirmed as current"), 409);
        if (currentStopId is not null)
            return ApiResult.Fail(new Error("wrong_stop_order", "a different stop is already current"), 409);
        if (await repo.GetTripRouteIdAsync(tripId, ct) is not { } routeId)
            return ApiResult.Fail(new Error("no_route", "trip has no route"), 409);
        var next = await repo.GetNextIncompleteStopAsync(tripId, routeId, ct);
        if (next is null || next.Id != stopId)
            return ApiResult.Fail(new Error("wrong_stop_order", "stops must be confirmed in sequence"), 409);

        var busId = await repo.GetBusIdAsync(tripId, ct);
        // Authoritative server-side re-check of GPS proximity — stronger than the merely-
        // advisory WithinArrivalRadius signal computed during ping ingest (IngestPingsAsync).
        // Reuses the exact same lat/lng lookup and radius config as that call site. A trip
        // with no known location at all (no bus resolved, or no ping ever ingested) is
        // rejected rather than waved through — silently skipping this check would let a
        // driver bypass proximity validation entirely just by never sending a GPS ping,
        // which defeats the whole point of an authoritative server-side check.
        if (busId is not { } bid)
            return ApiResult.Fail(new Error("no_location", "cannot confirm arrival without a known GPS location"), 409);
        var snapshot = await buses.GetLiveSnapshotAsync(bid, ct);
        if (snapshot.Lat is not { } lat || snapshot.Lng is not { } lng)
            return ApiResult.Fail(new Error("no_location", "cannot confirm arrival without a known GPS location"), 409);
        var distance = TripRepository.Haversine(lat, lng, next.Lat, next.Lng);
        if (!StopArrivalRules.IsWithinRadius(distance, _arrivalRadiusMeters))
            return ApiResult.Fail(new Error("too_far", "you are not close enough to this stop to confirm arrival"), 409);

        // Final atomic guard against the check-then-act race: the pre-checks above can both pass for
        // a driver and a conductor tapping "Arrived" at the same instant. Only the caller that
        // actually claims the current stop (1 row) broadcasts; the loser gets the same 409 the
        // already-current pre-check returns and, crucially, does not emit a duplicate fleet event.
        if (await repo.ConfirmStopArrivalAsync(tid, tripId, stopId, next.Seq, clock.UtcNow, clock.UtcNow, ct) == 0)
            return ApiResult.Fail(new Error("already_at_stop", "this stop is already confirmed as current"), 409);
        await fleetBroadcaster.BroadcastStopArrivedAsync(bid, tripId, stopId, next.Name, clock.UtcNow, ct);
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> CompleteStopAsync(Guid tripId, Guid stopId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult.Fail(new Error("forbidden", "not your trip"), 403);
        if (!await repo.IsActiveAsync(tripId, ct))
            return ApiResult.Fail(new Error("trip_ended", "this trip has already ended"), 409);
        if (await repo.GetCurrentStopIdAsync(tripId, ct) != stopId)
            return ApiResult.Fail(new Error("not_current_stop", "this stop is not the confirmed current stop"), 409);

        // Final atomic guard against the same check-then-act race as ConfirmStopArrivalAsync: only
        // the caller that actually releases the current stop (1 row) broadcasts the completion; a
        // concurrent/duplicate depart gets the same 409 the not-current pre-check returns.
        if (await repo.CompleteStopAsync(tid, tripId, stopId, clock.UtcNow, ct) == 0)
            return ApiResult.Fail(new Error("not_current_stop", "this stop is not the confirmed current stop"), 409);
        if (await repo.GetBusIdAsync(tripId, ct) is { } busId && await repo.GetTripRouteIdAsync(tripId, ct) is { } routeId)
        {
            var next = await repo.GetNextIncompleteStopAsync(tripId, routeId, ct);
            await fleetBroadcaster.BroadcastStopCompletedAsync(busId, tripId, stopId, next?.Id, next?.Name, clock.UtcNow, ct);
        }
        return ApiResult.NoContent();
    }

    public async Task<ApiResult> MarkSchoolArrivedAsync(Guid tripId, CancellationToken ct = default)
    {
        if (tenant.TenantId is not { } tid || tenant.UserId is not { } uid)
            return ApiResult.Fail(new Error("forbidden", "no tenant/user context"), 403);
        if (await repo.GetParticipantRoleAsync(tid, tripId, uid, ct) is null)
            return ApiResult.Fail(new Error("forbidden", "not your trip"), 403);
        if (!await repo.IsPickupTripInProgressAsync(tripId, ct))
            return ApiResult.Fail(new Error("invalid_state", "not a pickup trip in progress"), 409);

        // Persist the triggering GPS location (the trip's last known ping) alongside the
        // arrival timestamp — same lookup pattern as ConfirmStopArrivalAsync/IngestPingsAsync.
        double? lat = null, lng = null;
        if (await repo.GetBusIdAsync(tripId, ct) is { } bid)
        {
            var snapshot = await buses.GetLiveSnapshotAsync(bid, ct);
            lat = snapshot.Lat;
            lng = snapshot.Lng;
        }
        await repo.MarkSchoolArrivedAsync(tid, tripId, clock.UtcNow, lat, lng, ct);
        if (await repo.GetBusIdAsync(tripId, ct) is { } busId)
        {
            var onboard = await repo.CountBoardedAsync(tripId, ct);
            await fleetBroadcaster.BroadcastSchoolArrivedAsync(busId, tripId, clock.UtcNow, onboard, ct);
        }
        return ApiResult.NoContent();
    }

    private TripResponse WithActiveBroadcaster(TripResponse trip) =>
        trip with { ActiveBroadcaster = TripBroadcasterRules.Compute(trip.DriverLastPingAt, trip.ConductorLastPingAt, clock.UtcNow) };
}
