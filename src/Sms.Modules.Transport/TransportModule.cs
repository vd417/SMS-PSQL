using System.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Transport;

// ActiveBroadcaster is deliberately NOT a primary-constructor parameter: Dapper's record
// materialization requires an exact positional match between the constructor and the
// selected columns, so a computed-only field (never selected from the DB) must live outside
// the constructor as an init-only property instead, set afterwards via `with`.
public sealed record TripResponse(
    Guid Id, Guid TenantId, Guid? RouteId, string? BusNo, Guid? DriverId, Guid? ConductorId,
    string Direction, string Status, DateTime? StartedAt, DateTime? EndedAt,
    DateTime? DriverLastPingAt = null, DateTime? ConductorLastPingAt = null)
{
    public string? ActiveBroadcaster { get; init; }
}
public sealed record StartTripRequest(Guid? RouteId, string? BusNo, string Direction);
public sealed record PingItem(double Lat, double Lng, double SpeedKmh, double Heading, DateTime At, double? Accuracy = null);
public sealed record BulkPingRequest(IReadOnlyList<PingItem> Pings);
public sealed record TripSummaryResponse(Guid TripId, int DurationMin, double DistanceKm, int StopsCovered, int BoardedCount);
public sealed record BoardingResponse(Guid TripId, Guid StudentId, Guid? StopId, string State, DateTime At);
public sealed record BoardingRequest(Guid StudentId, Guid? StopId, string State, DateTime At);
public sealed record StaffStopResponse(Guid Id, string Name, double Lat, double Lng, int Seq, int? EtaMin);
public sealed record StaffRouteResponse(Guid Id, string Name, string BusNo, IReadOnlyList<StaffStopResponse> Stops);
public sealed record StaffTripAssignmentResponse(
    StaffRouteResponse Route, Guid BusId, string BusNo, string? ConductorName, string? Shift, int StudentsAssigned);
public sealed record StaffRosterStudentResponse(Guid Id, string Name, Guid? StopId, string? PhotoUrl);
public sealed record StaffBusRouteSummaryResponse(string BusNo, string RouteName, string? Shift, int StudentsAssigned);
public sealed record StaleTripRow(Guid TripId, Guid BusId, Guid TenantId, DateTime? LastPingAt);

public sealed class TripRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string TripCols =
        "Id, TenantId, RouteId, BusNo, DriverId, ConductorId, Direction, Status, StartedAt, EndedAt, " +
        "DriverLastPingAt, ConductorLastPingAt";
    private sealed record PingRow(double Lat, double Lng);

    public Task<TripResponse?> StartAsync(Guid tenantId, Guid driverId, StartTripRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<TripResponse>("dbo.Trip_Start",
            new { TenantId = tenantId, r.RouteId, r.BusNo, DriverId = driverId, r.Direction }, ct);

    public async Task<TripResponse?> GetCurrentAsync(Guid userId, CancellationToken ct = default) =>
        (await QueryInlineAsync<TripResponse>(
            // 'arrived' is still an active trip — it has reached school but not yet ended (a
            // return/drop leg may follow) — so it must stay visible here or the driver's own
            // app could never see the trip again in order to end it.
            $"SELECT TOP 1 {TripCols} FROM dbo.Trips WHERE (DriverId = @userId OR ConductorId = @userId) AND Status IN ('live', 'arrived') ORDER BY StartedAt DESC",
            new { userId }, ct)).FirstOrDefault();

    private sealed record TripParticipantsRow(Guid? DriverId, Guid? ConductorId);

    /// Returns "driver", "conductor", or null if the caller is neither — the trip's driver or
    /// its assigned conductor may operate it, RLS already scopes the row to the caller's tenant.
    /// Guards driver-app mutations against acting on a peer's trip within the same school.
    public async Task<string?> GetParticipantRoleAsync(Guid tenantId, Guid tripId, Guid userId, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<TripParticipantsRow>(
            "SELECT DriverId, ConductorId FROM dbo.Trips WHERE Id = @tripId AND TenantId = @tenantId",
            new { tripId, tenantId }, ct)).FirstOrDefault();
        if (row is null) return null;
        if (row.DriverId == userId) return "driver";
        if (row.ConductorId == userId) return "conductor";
        return null;
    }

    private sealed record ActiveTripParticipantsRow(Guid? DriverId, Guid? ConductorId);

    /// Returns "driver"/"conductor" if the caller is a participant of this bus's
    /// currently-live trip, else null. Bus-keyed (not trip-keyed) so a caller can
    /// check "am I driving/conducting this bus right now" without already
    /// knowing today's TripId.
    public async Task<string?> GetActiveDriverOrConductorRoleByBusAsync(Guid busId, Guid userId, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<ActiveTripParticipantsRow>(
            // Include 'arrived': a trip that reached school but hasn't ended is still this
            // bus's active trip, and its driver/conductor still own it.
            "SELECT TOP 1 DriverId, ConductorId FROM dbo.Trips WHERE BusId = @busId AND Status IN ('live', 'arrived') ORDER BY StartedAt DESC",
            new { busId }, ct)).FirstOrDefault();
        if (row is null) return null;
        if (row.DriverId == userId) return "driver";
        if (row.ConductorId == userId) return "conductor";
        return null;
    }

    /// The bus a trip belongs to — used to know which SignalR bus-group to
    /// broadcast a position/lifecycle event to after a trip mutation.
    public async Task<Guid?> GetBusIdAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>("SELECT BusId FROM dbo.Trips WHERE Id = @tripId", new { tripId }, ct)).FirstOrDefault();

    /// 'arrived' still counts as active (matches GetCurrentAsync) — only a trip that has actually
    /// ended should reject further mutations (pings, boarding, stop confirm/complete).
    public async Task<bool> IsActiveAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<string>(
            "SELECT Status FROM dbo.Trips WHERE Id = @tripId AND Status IN ('live', 'arrived')",
            new { tripId }, ct)).Any();

    public Task IngestPingsAsync(Guid tenantId, Guid tripId, IReadOnlyList<PingItem> pings, CancellationToken ct = default)
    {
        var table = new DataTable();
        table.Columns.Add("Lat", typeof(double));
        table.Columns.Add("Lng", typeof(double));
        table.Columns.Add("SpeedKmh", typeof(double));
        table.Columns.Add("Heading", typeof(double));
        table.Columns.Add("At", typeof(DateTime));
        table.Columns.Add("Accuracy", typeof(double));
        foreach (var p in pings) table.Rows.Add(p.Lat, p.Lng, p.SpeedKmh, p.Heading, p.At, (object?)p.Accuracy ?? DBNull.Value);

        var args = new DynamicParameters();
        args.Add("@TenantId", tenantId);
        args.Add("@TripId", tripId);
        args.Add("@Rows", table.AsTableValuedParameter("dbo.TripPingTvp"));
        return ExecuteProcAsync("dbo.TripPing_BulkInsert", args, ct);
    }

    public Task MarkPingAsync(Guid tripId, string role, CancellationToken ct = default)
    {
        var column = role == "driver" ? "DriverLastPingAt" : "ConductorLastPingAt";
        return ExecuteInlineAsync(
            $"UPDATE dbo.Trips SET {column} = SYSUTCDATETIME() WHERE Id = @tripId", new { tripId }, ct);
    }

    public async Task<TripSummaryResponse> EndAsync(Guid tenantId, Guid tripId, CancellationToken ct = default)
    {
        var trip = await QuerySingleProcAsync<TripResponse>("dbo.Trip_End", new { Id = tripId, TenantId = tenantId }, ct);
        var pings = await QueryInlineAsync<PingRow>(
            "SELECT Lat, Lng FROM dbo.TripPings WHERE TripId = @tripId ORDER BY At", new { tripId }, ct);

        double metres = 0;
        for (var i = 1; i < pings.Count; i++)
            metres += Haversine(pings[i - 1].Lat, pings[i - 1].Lng, pings[i].Lat, pings[i].Lng);

        var durationMin = trip is { StartedAt: { } s, EndedAt: { } e } ? (int)(e - s).TotalMinutes : 0;
        var boarded = await CountBoardedAsync(tripId, ct);
        var stops = (await QueryInlineAsync<int>(
            "SELECT COUNT(DISTINCT StopId) FROM dbo.Boardings WHERE TripId = @tripId AND StopId IS NOT NULL",
            new { tripId }, ct)).First();

        return new TripSummaryResponse(tripId, durationMin, Math.Round(metres / 1000, 2), stops, boarded);
    }

    private sealed record AssignedBusRow(Guid BusId, string BusNo, Guid? RouteId, string? ConductorName, string? Shift);
    private sealed record RouteRow(Guid Id, string Name);

    /// Resolved by the driver's own identity (Staff.UserId -> Buses.DriverStaffId), never by a
    /// client-supplied id, so the Trip screen's first load needs nothing but the caller's JWT.
    public async Task<StaffTripAssignmentResponse?> GetAssignmentAsync(Guid driverUserId, CancellationToken ct = default)
    {
        var bus = (await QueryInlineAsync<AssignedBusRow>(
            @"SELECT b.Id AS BusId, b.BusNo, b.RouteId, cs.Name AS ConductorName, s.Shift
              FROM dbo.Buses b
              JOIN dbo.Staff s ON s.Id = b.DriverStaffId
              LEFT JOIN dbo.Staff cs ON cs.Id = b.ConductorStaffId
              WHERE s.UserId = @driverUserId", new { driverUserId }, ct)).FirstOrDefault();
        if (bus?.RouteId is not { } routeId) return null;

        var route = (await QueryInlineAsync<RouteRow>(
            "SELECT Id, Name FROM dbo.TransportRoutes WHERE Id = @routeId", new { routeId }, ct)).FirstOrDefault();
        if (route is null) return null;

        var stops = await QueryInlineAsync<StaffStopResponse>(
            "SELECT Id, Name, Lat, Lng, Seq, CAST(NULL AS int) AS EtaMin FROM dbo.RouteStops WHERE RouteId = @routeId ORDER BY Seq",
            new { routeId }, ct);

        var studentsAssigned = (await QueryInlineAsync<int>(
            "SELECT COUNT(*) FROM dbo.StudentBusAssignments WHERE BusId = @busId", new { busId = bus.BusId }, ct)).First();

        return new StaffTripAssignmentResponse(
            new StaffRouteResponse(route.Id, route.Name, bus.BusNo, stops), bus.BusId, bus.BusNo, bus.ConductorName, bus.Shift, studentsAssigned);
    }

    /// Lightweight bus+route lookup for the staff dashboard's role card — driver/conductor
    /// resolved from their own identity, matching whichever of DriverStaffId/ConductorStaffId
    /// applies. Unlike GetAssignmentAsync, doesn't need stops or the peer's name.
    public async Task<StaffBusRouteSummaryResponse?> GetDriverBusRouteAsync(Guid driverUserId, CancellationToken ct = default) =>
        (await QueryInlineAsync<StaffBusRouteSummaryResponse>(
            @"SELECT b.""BusNo"" AS ""BusNo"", r.""Name"" AS ""RouteName"", s.""Shift"" AS ""Shift"",
                (SELECT CAST(COUNT(*) AS int) FROM ""dbo"".""StudentBusAssignments"" sba WHERE sba.""BusId"" = b.""Id"") AS ""StudentsAssigned""
              FROM ""dbo"".""Buses"" b
              JOIN ""dbo"".""Staff"" s ON s.""Id"" = b.""DriverStaffId""
              JOIN ""dbo"".""TransportRoutes"" r ON r.""Id"" = b.""RouteId""
              WHERE s.""UserId"" = @driverUserId", new { driverUserId }, ct)).FirstOrDefault();

    /// True when userId is the driver or conductor currently structurally assigned to busId
    /// (dbo.Buses.DriverStaffId/ConductorStaffId, resolved via the caller's own dbo.Staff row) —
    /// the same assignment concept GetAssignmentAsync uses for a driver's trip dashboard,
    /// generalized to accept an explicit busId and to also match conductors. RLS on both
    /// dbo.Buses and dbo.Staff means a cross-tenant busId (or a caller with no Staff row at all,
    /// e.g. a non-staff role) simply resolves to false here rather than throwing. Used by
    /// VehicleChecks (inspections/fuel logs) to gate submission to the caller's real assigned
    /// bus instead of trusting a client-supplied busId.
    public async Task<bool> IsDriverOrConductorAssignedToBusAsync(Guid userId, Guid busId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            @"SELECT COUNT(1) FROM dbo.Buses b
              WHERE b.Id = @busId
                AND EXISTS (
                  SELECT 1 FROM dbo.Staff s
                  WHERE s.UserId = @userId AND (s.Id = b.DriverStaffId OR s.Id = b.ConductorStaffId))",
            new { userId, busId }, ct)).First() > 0;

    public async Task<StaffBusRouteSummaryResponse?> GetConductorBusRouteAsync(Guid conductorUserId, CancellationToken ct = default) =>
        (await QueryInlineAsync<StaffBusRouteSummaryResponse>(
            @"SELECT b.""BusNo"" AS ""BusNo"", r.""Name"" AS ""RouteName"", s.""Shift"" AS ""Shift"",
                (SELECT CAST(COUNT(*) AS int) FROM ""dbo"".""StudentBusAssignments"" sba WHERE sba.""BusId"" = b.""Id"") AS ""StudentsAssigned""
              FROM ""dbo"".""Buses"" b
              JOIN ""dbo"".""Staff"" s ON s.""Id"" = b.""ConductorStaffId""
              JOIN ""dbo"".""TransportRoutes"" r ON r.""Id"" = b.""RouteId""
              WHERE s.""UserId"" = @conductorUserId", new { conductorUserId }, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<StaffRosterStudentResponse>> GetRosterAsync(Guid tripId, CancellationToken ct = default)
    {
        var busId = (await QueryInlineAsync<Guid?>(
            "SELECT BusId FROM dbo.Trips WHERE Id = @tripId", new { tripId }, ct)).FirstOrDefault();
        if (busId is null) return [];

        return await QueryInlineAsync<StaffRosterStudentResponse>(
            @"SELECT s.Id, s.Name, sba.StopId, s.PhotoUrl
              FROM dbo.StudentBusAssignments sba
              JOIN dbo.Students s ON s.Id = sba.StudentId
              WHERE sba.BusId = @busId
              ORDER BY s.Name", new { busId }, ct);
    }

    public Task<IReadOnlyList<BoardingResponse>> ListBoardingAsync(Guid tripId, CancellationToken ct = default) =>
        QueryInlineAsync<BoardingResponse>(
            "SELECT TripId, StudentId, StopId, State, At FROM dbo.Boardings WHERE TripId = @tripId ORDER BY At",
            new { tripId }, ct);

    public Task UpsertBoardingAsync(Guid tenantId, Guid tripId, BoardingRequest r, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.Boarding_Upsert",
            new { TenantId = tenantId, TripId = tripId, r.StudentId, r.StopId, r.State, r.At }, ct);

    /// Every currently-live trip whose most recent driver-or-conductor ping is
    /// older than staleAfter (or has never pinged at all). Runs under a
    /// platform-level ITenantContext (IsPlatform = true) since this must scan
    /// across every tenant, mirroring AbsenceAlertWorker's cross-tenant sweep
    /// pattern — verify against src/Sms.Api/Workers/AbsenceAlertWorker.cs that
    /// rls.fn_tenant_predicate actually bypasses RLS filtering when IsPlatform
    /// is true before relying on this in production.
    public async Task<IReadOnlyList<StaleTripRow>> GetStaleActiveTripsAsync(TimeSpan staleAfter, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<StaleTripRow>(
            @"SELECT Id AS TripId, BusId, TenantId,
                     (SELECT MAX(v) FROM (VALUES (DriverLastPingAt), (ConductorLastPingAt)) AS x(v)) AS LastPingAt
              FROM dbo.Trips
              WHERE Status = 'live' AND BusId IS NOT NULL", null, ct);
        var cutoff = DateTime.UtcNow - staleAfter;
        return rows.Where(r => r.LastPingAt is null || r.LastPingAt < cutoff).ToList();
    }

    public sealed record NextStopRow(Guid Id, string Name, double Lat, double Lng, int Seq);

    /// The trip's next stop (by Seq) that has no TripStopProgress row with a
    /// DepartedAt set — i.e. not yet completed. Null once every stop on the
    /// route has been completed. Stops must be confirmed/completed in this
    /// order; the confirm-arrival endpoint (Task 5) rejects out-of-order calls.
    public async Task<NextStopRow?> GetNextIncompleteStopAsync(Guid tripId, Guid routeId, CancellationToken ct = default) =>
        (await QueryInlineAsync<NextStopRow>(
            @"SELECT TOP 1 rs.Id, rs.Name, rs.Lat, rs.Lng, rs.Seq
              FROM dbo.RouteStops rs
              WHERE rs.RouteId = @routeId
                AND NOT EXISTS (
                    SELECT 1 FROM dbo.TripStopProgress tsp
                    WHERE tsp.TripId = @tripId AND tsp.StopId = rs.Id AND tsp.DepartedAt IS NOT NULL)
              ORDER BY rs.Seq",
            new { routeId, tripId }, ct)).FirstOrDefault();

    public Task ConfirmStopArrivalAsync(Guid tenantId, Guid tripId, Guid stopId, int seq, DateTime arrivedAt, DateTime confirmedAt, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.TripStopProgress_ConfirmArrival",
            new { TenantId = tenantId, TripId = tripId, StopId = stopId, Seq = seq, ArrivedAt = arrivedAt, ConfirmedAt = confirmedAt }, ct);

    public Task CompleteStopAsync(Guid tenantId, Guid tripId, Guid stopId, DateTime departedAt, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.TripStopProgress_Complete",
            new { TenantId = tenantId, TripId = tripId, StopId = stopId, DepartedAt = departedAt }, ct);

    public async Task<Guid?> GetCurrentStopIdAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>("SELECT CurrentStopId FROM dbo.Trips WHERE Id = @tripId", new { tripId }, ct)).FirstOrDefault();

    /// True when this trip is a still-live pickup trip — the only state a driver may mark
    /// "school-arrived" from (a drop trip, or a trip already ended/arrived, is rejected).
    public async Task<bool> IsPickupTripInProgressAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            "SELECT COUNT(1) FROM dbo.Trips WHERE Id = @tripId AND Direction = 'pickup' AND Status = 'live'",
            new { tripId }, ct)).First() > 0;

    /// Marks the school-arrival milestone without closing the trip — EndAsync remains the only
    /// way to actually end it, matching this feature's "arrived != ended" distinction. Persists
    /// the arrival timestamp and the triggering GPS location (the trip's last known ping,
    /// resolved by the caller — TripService.MarkSchoolArrivedAsync — the same way
    /// IngestPingsAsync/ConfirmStopArrivalAsync already resolve "where is this trip right now").
    public Task MarkSchoolArrivedAsync(Guid tenantId, Guid tripId, DateTime at, double? lat, double? lng, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            @"UPDATE dbo.Trips
              SET Status = 'arrived', SchoolArrivedAt = @at, SchoolArrivedLat = @lat, SchoolArrivedLng = @lng
              WHERE Id = @tripId AND TenantId = @tenantId",
            new { tripId, tenantId, at, lat, lng }, ct);

    /// Shared by EndAsync's trip-summary and MarkSchoolArrivedAsync's broadcast payload — kept as
    /// one named query so both call sites can't drift on what counts as "boarded".
    public async Task<int> CountBoardedAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            "SELECT COUNT(*) FROM dbo.Boardings WHERE TripId = @tripId AND State = 'boarded'", new { tripId }, ct)).First();

    /// No existing method already returns a trip's RouteId on its own (TripResponse carries it,
    /// but only as part of the full row); TripService.IngestPingsAsync needs it in isolation to
    /// call GetNextIncompleteStopAsync without re-fetching the whole trip.
    public async Task<Guid?> GetTripRouteIdAsync(Guid tripId, CancellationToken ct = default) =>
        (await QueryInlineAsync<Guid?>("SELECT RouteId FROM dbo.Trips WHERE Id = @tripId", new { tripId }, ct)).FirstOrDefault();

    /// Public (not private) so TripService's stop-arrival-radius computation (Task 4) can reuse
    /// the same distance formula instead of duplicating a fourth Haversine implementation
    /// alongside this one, BusModule's, and AttendanceModule's.
    public static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double radius = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public static class TransportModule
{
    public static IServiceCollection AddTransportModule(this IServiceCollection services)
    {
        services.AddScoped<TripRepository>();
        return services;
    }
}
