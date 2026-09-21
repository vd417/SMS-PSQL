using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Transport;

/// One student's bus assignment, as shown on the admin roster for a bus.
public sealed record StudentBusAssignmentResponse(
    Guid StudentId, string StudentName, string Initials, string AdmissionNo,
    Guid BusId, string BusNo, string? RouteName, Guid? StopId, string? StopName);

/// Live position of a parent's child bus (post status-derivation), for the parent app.
/// <c>Status</c> keeps the legacy idle/on_route/at_stop/delayed values; <c>TrackingStatus</c>
/// is the canonical LIVE/DELAYED/OFFLINE value every client should render.
public sealed record ChildBusPositionResponse(
    Guid StudentId, string StudentName, string AdmissionNo,
    Guid? BusId, string? BusNo, string? RouteName, string Status,
    double? Lat, double? Lng, double? SpeedKmh, string? NextStopName, DateTime? LastPingAt,
    Guid? StudentStopId = null, string? StudentStopName = null,
    double? StudentStopLat = null, double? StudentStopLng = null,
    double? DistanceToStudentStopM = null,
    IReadOnlyList<BusStopResponse>? RouteStops = null,
    string? Grade = null, string? Section = null,
    string? Driver = null, string? DriverPhone = null,
    string TrackingStatus = BusTrackingStatusRules.Offline,
    string? Motion = null,
    string Assignment = BusTrackingStatusRules.None,
    string? BoardingState = null,
    int? EtaNextStopMin = null,
    int? CurrentStopIndex = null,
    string? CurrentStopName = null,
    int? PassedStopCount = null,
    int? TotalStops = null,
    int? EtaToStudentStopMin = null,
    Guid? RouteId = null);

/// Raw per-child live row before status derivation.
/// Class (not positional record): Dapper cannot bind LEFT JOIN nulls onto a Guid/DateTime
/// constructor when SQL Server reports those columns as non-nullable uniqueidentifier/datetime2.
public sealed class ChildBusRow
{
    public Guid StudentId { get; init; }
    public string StudentName { get; init; } = "";
    public string AdmissionNo { get; init; } = "";
    public Guid? BusId { get; init; }
    public string? BusNo { get; init; }
    public string? RouteName { get; init; }
    public Guid? TripId { get; init; }
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public double? SpeedKmh { get; init; }
    public DateTime? LastPingAt { get; init; }
    public Guid? StopId { get; init; }
    public string? StopName { get; init; }
    public double? StopLat { get; init; }
    public double? StopLng { get; init; }
    public string? Grade { get; init; }
    public string? Section { get; init; }
    public string? Driver { get; init; }
    public string? DriverPhone { get; init; }
    public Guid? RouteId { get; init; }
    public Guid? AssignmentId { get; init; }
    public int OptedOut { get; init; }
    public string? BoardingState { get; init; }
}

/// Student on a bus with their assigned stop, used to fan out parent approach alerts.
public sealed record BusRiderStopRow(
    Guid StudentId, string StudentName, string AdmissionNo, string BusNo,
    Guid? StopId, string? StopName, double? StopLat, double? StopLng);

/// Current transport mapping for a student (route/stop/fee head chosen, bus possibly pending). Consumed by Task 3's StudentTransportService.
public sealed record TransportStatusRow(Guid? BusId, Guid? RouteId, Guid? StopId, Guid? FeeHeadId);

/// One tenant-wide row of "this student's transport assignment carries this fee head" — used by
/// invoice generation to decide, per student, whether a transport-flagged fee head applies. Bulk
/// query (one row per student with an active FeeHeadId), not one lookup per student.
public sealed record StudentTransportFeeHeadRow(Guid StudentId, Guid FeeHeadId);

/// One row of the admin "Transport Students" list: a student's route/stop/fee-head/bus mapping, with
/// MappingStatus "mapped" (has a bus) or "pending" (opted in but not yet auto/manually assigned a bus).
public sealed record TransportMappedStudentResponse(
    Guid StudentId, string StudentName, string AdmissionNo, string? Grade, string? Section,
    Guid? FeeHeadId, string? FeeHeadName, Guid? RouteId, string? RouteName, Guid? StopId, string? StopName,
    Guid? BusId, string? BusNo, string? Driver, string? ConductorName, int? Capacity, int BusOccupied,
    string MappingStatus);

/// Query-string filters for the Transport Students list endpoint.
public sealed record TransportStudentsFilter(
    Guid? RouteId, Guid? StopId, Guid? BusId, string? Grade, Guid? FeeHeadId, string? Status);

public sealed class StudentBusRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private sealed record AssignmentRow(
        Guid StudentId, string StudentName, string AdmissionNo,
        Guid BusId, string BusNo, string? RouteName, Guid? StopId, string? StopName);

    public Task AssignAsync(Guid tenantId, Guid studentId, Guid busId, Guid? stopId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.StudentBus_Assign",
            new { TenantId = tenantId, StudentId = studentId, BusId = busId, StopId = stopId }, ct);

    public Task UnassignAsync(Guid tenantId, Guid studentId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.StudentBus_Unassign",
            new { TenantId = tenantId, StudentId = studentId }, ct);

    public Task OptInAsync(Guid tenantId, Guid studentId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.StudentTransport_OptIn", new { TenantId = tenantId, StudentId = studentId }, ct);

    public Task OptOutAsync(Guid tenantId, Guid studentId, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.StudentTransport_OptOut", new { TenantId = tenantId, StudentId = studentId }, ct);

    public Task UpsertTransportAsync(
        Guid tenantId, Guid studentId, Guid routeId, Guid? stopId, Guid? feeHeadId, Guid? busId,
        CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.StudentTransport_Upsert",
            new { TenantId = tenantId, StudentId = studentId, RouteId = routeId, StopId = stopId, FeeHeadId = feeHeadId, BusId = busId },
            ct);

    public async Task<TransportStatusRow?> GetTransportStatusAsync(Guid studentId, CancellationToken ct = default) =>
        (await QueryInlineAsync<TransportStatusRow>(
            """SELECT "BusId", "RouteId", "StopId", "FeeHeadId" FROM "dbo"."StudentBusAssignments" WHERE "StudentId" = @studentId""",
            new { studentId }, ct)).FirstOrDefault();

    /// One query for every student in the tenant with an active transport-fee-head assignment —
    /// used by invoice generation, which must not issue a per-student lookup for this.
    public Task<IReadOnlyList<StudentTransportFeeHeadRow>> ListActiveFeeHeadIdsAsync(CancellationToken ct = default) =>
        QueryInlineAsync<StudentTransportFeeHeadRow>(
            """SELECT "StudentId", "FeeHeadId" FROM "dbo"."StudentBusAssignments" WHERE "FeeHeadId" IS NOT NULL""", ct: ct);

    public async Task<IReadOnlyList<TransportMappedStudentResponse>> ListMappedAsync(
        TransportStudentsFilter filter, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<TransportMappedStudentResponse>(
            @"SELECT s.Id AS StudentId, s.Name AS StudentName, s.AdmissionNo, s.Grade, s.Section,
                     sba.FeeHeadId, fh.Name AS FeeHeadName,
                     sba.RouteId, r.Name AS RouteName,
                     sba.StopId, COALESCE(rs.Name, bs.Name) AS StopName,
                     sba.BusId, b.BusNo, b.Driver, cs.Name AS ConductorName, b.Capacity,
                     ISNULL((SELECT COUNT(*) FROM dbo.StudentBusAssignments x WHERE x.BusId = b.Id), 0) AS BusOccupied,
                     CASE WHEN sba.BusId IS NOT NULL THEN 'mapped' ELSE 'pending' END AS MappingStatus
              FROM dbo.StudentBusAssignments sba
              JOIN dbo.Students s ON s.Id = sba.StudentId
              LEFT JOIN dbo.TransportRoutes r ON r.Id = sba.RouteId
              LEFT JOIN dbo.RouteStops rs ON rs.Id = sba.StopId
              LEFT JOIN dbo.BusStops bs ON bs.Id = sba.StopId
              LEFT JOIN dbo.Buses b ON b.Id = sba.BusId
              LEFT JOIN dbo.Staff cs ON cs.Id = b.ConductorStaffId
              LEFT JOIN dbo.FeeHeads fh ON fh.Id = sba.FeeHeadId
              WHERE (@RouteId IS NULL OR sba.RouteId = @RouteId)
                AND (@StopId IS NULL OR sba.StopId = @StopId)
                AND (@BusId IS NULL OR sba.BusId = @BusId)
                AND (@Grade IS NULL OR s.Grade = @Grade)
                AND (@FeeHeadId IS NULL OR sba.FeeHeadId = @FeeHeadId)
                AND (@Status IS NULL
                     OR (@Status = 'mapped' AND sba.BusId IS NOT NULL)
                     OR (@Status = 'pending' AND sba.BusId IS NULL))
              ORDER BY s.Name",
            new
            {
                filter.RouteId, filter.StopId, filter.BusId, filter.Grade, filter.FeeHeadId, filter.Status,
            }, ct);
        return rows;
    }

    // RLS-scoped existence guards: a caller can never see another tenant's bus/student,
    // so these double as cross-tenant reference protection before an upsert.
    public async Task<bool> BusExistsAsync(Guid busId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            "SELECT COUNT(1) FROM dbo.Buses WHERE Id = @busId", new { busId }, ct)).First() > 0;

    public async Task<bool> StudentExistsAsync(Guid studentId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            """SELECT COUNT(1) FROM "dbo"."Students" WHERE "Id" = @studentId""", new { studentId }, ct)).First() > 0;

    public async Task<bool> IsStudentOnBusAsync(Guid studentId, Guid busId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>(
            "SELECT COUNT(1) FROM dbo.StudentBusAssignments WHERE StudentId = @studentId AND BusId = @busId",
            new { studentId, busId }, ct)).First() > 0;

    public async Task<IReadOnlyList<StudentBusAssignmentResponse>> ListByBusAsync(Guid busId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<AssignmentRow>(
            @"SELECT sba.StudentId, s.Name AS StudentName, s.AdmissionNo,
                     sba.BusId, b.BusNo, b.RouteName, sba.StopId,
                     COALESCE(rs.Name, bs.Name) AS StopName
              FROM dbo.StudentBusAssignments sba
              JOIN dbo.Students s ON s.Id = sba.StudentId
              JOIN dbo.Buses b ON b.Id = sba.BusId
              LEFT JOIN dbo.RouteStops rs ON rs.Id = sba.StopId
              LEFT JOIN dbo.BusStops bs ON bs.Id = sba.StopId
              WHERE sba.BusId = @busId ORDER BY s.Name", new { busId }, ct);
        return rows.Select(r => new StudentBusAssignmentResponse(
            r.StudentId, r.StudentName, BusRepository.Initials(r.StudentName), r.AdmissionNo,
            r.BusId, r.BusNo, r.RouteName, r.StopId, r.StopName)).ToList();
    }

    /// The live bus for each student whose AdmissionNo matches (RLS scopes this to the caller's tenant,
    /// so identical admission numbers in other schools are never returned).
    public Task<IReadOnlyList<ChildBusRow>> ChildrenBusByAdmissionAsync(string admissionNo, CancellationToken ct = default) =>
        QueryInlineAsync<ChildBusRow>(ChildBusSelect + " WHERE s.AdmissionNo = @admissionNo ORDER BY s.Name",
            new { admissionNo }, ct);

    public Task<IReadOnlyList<ChildBusRow>> ChildrenBusByStudentIdsAsync(
        IReadOnlyList<Guid> studentIds, CancellationToken ct = default)
    {
        if (studentIds.Count == 0) return Task.FromResult<IReadOnlyList<ChildBusRow>>([]);
        return QueryInlineAsync<ChildBusRow>(
            ChildBusSelect + " WHERE s.Id IN @studentIds ORDER BY s.Name",
            new { studentIds }, ct);
    }

    public Task<IReadOnlyList<Guid>> ListLinkedStudentIdsAsync(Guid parentUserId, CancellationToken ct = default) =>
        QueryInlineAsync<Guid>(
            "SELECT StudentId FROM dbo.ParentStudentLinks WHERE ParentUserId = @parentUserId",
            new { parentUserId }, ct);

    /// Distinct bus IDs for a parent's linked children (and legacy admission-linked child).
    public Task<IReadOnlyList<Guid>> ListDistinctBusIdsForParentAsync(Guid parentUserId, CancellationToken ct = default) =>
        QueryInlineAsync<Guid>(@"
SELECT DISTINCT BusId FROM (
    SELECT sba.BusId
    FROM dbo.ParentStudentLinks l
    JOIN dbo.StudentBusAssignments sba ON sba.StudentId = l.StudentId
    WHERE l.ParentUserId = @parentUserId AND sba.BusId IS NOT NULL
    UNION
    SELECT sba.BusId
    FROM dbo.Users u
    JOIN dbo.Students s ON s.AdmissionNo = u.StudentId AND s.TenantId = u.TenantId
    JOIN dbo.StudentBusAssignments sba ON sba.StudentId = s.Id
    WHERE u.Id = @parentUserId AND u.StudentId IS NOT NULL AND sba.BusId IS NOT NULL
) x", new { parentUserId }, ct);

    public Task<IReadOnlyList<Guid>> ListParentUserIdsAsync(Guid studentId, string admissionNo, CancellationToken ct = default) =>
        QueryInlineAsync<Guid>(@"
SELECT DISTINCT Id FROM (
    SELECT ParentUserId AS Id FROM dbo.ParentStudentLinks WHERE StudentId = @studentId
    UNION
    SELECT u.Id FROM dbo.Users u WHERE u.StudentId = @admissionNo
) p", new { studentId, admissionNo }, ct);

    public Task<IReadOnlyList<BusRiderStopRow>> ListRidersWithStopsAsync(Guid busId, CancellationToken ct = default) =>
        QueryInlineAsync<BusRiderStopRow>(@"
SELECT s.Id AS StudentId, s.Name AS StudentName, s.AdmissionNo, b.BusNo,
       sba.StopId, COALESCE(rs.Name, bs.Name) AS StopName,
       COALESCE(rs.Lat, bs.Lat) AS StopLat, COALESCE(rs.Lng, bs.Lng) AS StopLng
FROM dbo.StudentBusAssignments sba
JOIN dbo.Students s ON s.Id = sba.StudentId
JOIN dbo.Buses b ON b.Id = sba.BusId
LEFT JOIN dbo.RouteStops rs ON rs.Id = sba.StopId
LEFT JOIN dbo.BusStops bs ON bs.Id = sba.StopId
WHERE sba.BusId = @busId
ORDER BY s.Name", new { busId }, ct);

    /// Returns true when this (trip, student, kind) had not been recorded yet.
    public async Task<bool> TryInsertParentAlertAsync(
        Guid tenantId, Guid tripId, Guid studentId, Guid parentUserId, string kind, CancellationToken ct = default)
    {
        var inserted = await ExecuteInlineAsync(@"
INSERT INTO dbo.BusParentAlerts (Id, TenantId, TripId, StudentId, ParentUserId, Kind)
SELECT NEWID(), @tenantId, @tripId, @studentId, @parentUserId, @kind
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.BusParentAlerts
    WHERE TripId = @tripId AND StudentId = @studentId AND ParentUserId = @parentUserId AND Kind = @kind
);
",
            new { tenantId, tripId, studentId, parentUserId, kind }, ct);
        return inserted > 0;
    }

    private const string ChildBusSelect = @"
SELECT s.Id AS StudentId, s.Name AS StudentName, s.AdmissionNo,
       s.Grade AS Grade, s.Section AS Section,
       b.Id AS BusId, b.BusNo, COALESCE(NULLIF(b.RouteName, ''), r.Name) AS RouteName,
       COALESCE(NULLIF(b.Driver, ''), ds.Name) AS Driver, b.DriverPhone,
       t.Id AS TripId, p.Lat, p.Lng, p.SpeedKmh, p.At AS LastPingAt,
       sba.StopId, COALESCE(rs.Name, bs.Name) AS StopName,
       COALESCE(rs.Lat, bs.Lat) AS StopLat, COALESCE(rs.Lng, bs.Lng) AS StopLng,
       r.Id AS RouteId, sba.Id AS AssignmentId,
       CASE WHEN oo.StudentId IS NOT NULL THEN 1 ELSE 0 END AS OptedOut,
       brd.State AS BoardingState
FROM dbo.Students s
LEFT JOIN dbo.StudentBusAssignments sba ON sba.StudentId = s.Id
LEFT JOIN dbo.Buses b ON b.Id = sba.BusId
-- Prefer the bus's actual current route once a bus is assigned — sba.RouteId
-- is the route chosen at opt-in time and can drift from reality after a
-- reassignment to a different bus/route, which otherwise fetches road
-- geometry for a route the student isn't really riding.
LEFT JOIN dbo.TransportRoutes r ON r.Id = COALESCE(b.RouteId, sba.RouteId)
LEFT JOIN dbo.Staff ds ON ds.Id = b.DriverStaffId
LEFT JOIN dbo.RouteStops rs ON rs.Id = sba.StopId
LEFT JOIN dbo.BusStops bs ON bs.Id = sba.StopId
LEFT JOIN dbo.StudentTransportOptOut oo ON oo.StudentId = s.Id
OUTER APPLY (
  SELECT TOP 1 tt.Id, tt.StartedAt FROM dbo.Trips tt
  WHERE tt.BusId = b.Id AND tt.Status IN ('live', 'arrived') ORDER BY tt.StartedAt DESC) t
OUTER APPLY (
  SELECT TOP 1 pp.Lat, pp.Lng, pp.SpeedKmh, pp.At FROM dbo.TripPings pp
  WHERE pp.TripId = t.Id ORDER BY pp.At DESC) p
OUTER APPLY (
  SELECT TOP 1 bo.State FROM dbo.Boardings bo
  WHERE bo.TripId = t.Id AND bo.StudentId = s.Id ORDER BY bo.At DESC) brd";

    /// True if a student with this admission number (RLS-scoped to the caller's
    /// tenant) is currently assigned to this bus. Used to authorize a parent's
    /// live-tracking subscription to their own child's bus only.
    public async Task<bool> HasChildOnBusAsync(string admissionNo, Guid busId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            @"SELECT COUNT(1) FROM dbo.Students s
              JOIN dbo.StudentBusAssignments sba ON sba.StudentId = s.Id
              WHERE s.AdmissionNo = @admissionNo AND sba.BusId = @busId",
            new { admissionNo, busId }, ct);
        return rows.FirstOrDefault() > 0;
    }

    /// True when any of this parent's linked children (ParentStudentLinks) is assigned to the bus.
    public async Task<bool> HasLinkedChildOnBusAsync(Guid parentUserId, Guid busId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            @"SELECT COUNT(1) FROM dbo.ParentStudentLinks l
              JOIN dbo.StudentBusAssignments sba ON sba.StudentId = l.StudentId
              WHERE l.ParentUserId = @parentUserId AND sba.BusId = @busId",
            new { parentUserId, busId }, ct);
        return rows.FirstOrDefault() > 0;
    }

    /// Route-level equivalent of <see cref="HasChildOnBusAsync"/>: a student's own
    /// StudentBusAssignments.RouteId can diverge from their assigned bus's Buses.RouteId
    /// (e.g. the bus's route column is stale/reassigned), so route-view authorization
    /// must not rely solely on the bus-side value.
    public async Task<bool> HasChildOnRouteAsync(string admissionNo, Guid routeId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            @"SELECT COUNT(1) FROM dbo.Students s
              JOIN dbo.StudentBusAssignments sba ON sba.StudentId = s.Id
              WHERE s.AdmissionNo = @admissionNo AND sba.RouteId = @routeId",
            new { admissionNo, routeId }, ct);
        return rows.FirstOrDefault() > 0;
    }

    /// Route-level equivalent of <see cref="HasLinkedChildOnBusAsync"/>.
    public async Task<bool> HasLinkedChildOnRouteAsync(Guid parentUserId, Guid routeId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<int>(
            @"SELECT COUNT(1) FROM dbo.ParentStudentLinks l
              JOIN dbo.StudentBusAssignments sba ON sba.StudentId = l.StudentId
              WHERE l.ParentUserId = @parentUserId AND sba.RouteId = @routeId",
            new { parentUserId, routeId }, ct);
        return rows.FirstOrDefault() > 0;
    }
}

public static class StudentBusModule
{
    public static IServiceCollection AddStudentBusModule(this IServiceCollection services)
    {
        services.AddScoped<StudentBusRepository>();
        return services;
    }
}
