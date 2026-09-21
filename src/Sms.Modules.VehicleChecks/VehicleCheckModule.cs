using Microsoft.Extensions.DependencyInjection;
using Sms.Shared.Kernel.Authz;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.VehicleChecks;

/// Pure/stateless helpers, safe to unit test in isolation — mirrors StaffRoleMapper /
/// TaskService.IsExactlyOneAssignmentTarget's "pure rule, tested directly" convention.
public static class VehicleInspectionRules
{
    /// The authoritative "all clear" flag is always computed server-side from the eight checklist
    /// items rather than trusted from the client's own AllOk claim (CreateInspectionRequest still
    /// carries the field for wire back-compat with the mobile app's local computation, but the
    /// service ignores it) — this is what decides whether managers get paged, so a stale or buggy
    /// client value must never suppress a real failure notification.
    public static bool ComputeAllOk(
        bool brakes, bool tyres, bool lights, bool horn,
        bool firstAidKit, bool fireExtinguisher, bool emergencyExit, bool fuelLevel) =>
        brakes && tyres && lights && horn && firstAidKit && fireExtinguisher && emergencyExit && fuelLevel;
}

public sealed record InspectionResponse(
    Guid Id, Guid TenantId, Guid BusId, Guid SubmittedByUserId,
    bool Brakes, bool Tyres, bool Lights, bool Horn,
    bool FirstAidKit, bool FireExtinguisher, bool EmergencyExit, bool FuelLevel,
    bool AllOk, string? Remarks, DateTime InspectionDate, DateTime CreatedAt);

public sealed record CreateInspectionRequest(
    Guid BusId, bool Brakes, bool Tyres, bool Lights, bool Horn,
    bool FirstAidKit, bool FireExtinguisher, bool EmergencyExit, bool FuelLevel,
    bool AllOk, string? Remarks);

public sealed record FuelLogResponse(
    Guid Id, Guid TenantId, Guid BusId, Guid RecordedByUserId,
    int OdometerKm, decimal FuelAddedLiters, DateTime RecordedAt);

public sealed record CreateFuelLogRequest(Guid BusId, int OdometerKm, decimal FuelAddedLiters);

public sealed class VehicleCheckRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    private const string InspectionCols =
        "\"Id\", \"TenantId\", \"BusId\", \"SubmittedByUserId\", \"Brakes\", \"Tyres\", \"Lights\", \"Horn\", \"FirstAidKit\", " +
        "\"FireExtinguisher\", \"EmergencyExit\", \"FuelLevel\", \"AllOk\", \"Remarks\", \"InspectionDate\", \"CreatedAt\"";
    private const string FuelLogCols =
        "\"Id\", \"TenantId\", \"BusId\", \"RecordedByUserId\", \"OdometerKm\", \"FuelAddedLiters\", \"RecordedAt\"";

    private sealed record UserIdRow(Guid Id);

    /// Upsert: dbo.VehicleInspection_Upsert MERGEs on (TenantId, BusId, InspectionDate) — a
    /// resubmission for the same bus on the same (server-computed, school-local) day updates the
    /// existing row rather than inserting a duplicate, enforced additionally by a DB unique
    /// constraint so this holds even under concurrent submissions.
    public Task<InspectionResponse?> UpsertInspectionAsync(
        Guid tenantId, Guid busId, Guid submittedByUserId, CreateInspectionRequest r, bool allOk,
        DateTime inspectionDate, CancellationToken ct = default) =>
        QuerySingleProcAsync<InspectionResponse>("dbo.VehicleInspection_Upsert", new
        {
            TenantId = tenantId,
            BusId = busId,
            SubmittedByUserId = submittedByUserId,
            r.Brakes,
            r.Tyres,
            r.Lights,
            r.Horn,
            r.FirstAidKit,
            r.FireExtinguisher,
            r.EmergencyExit,
            r.FuelLevel,
            AllOk = allOk,
            r.Remarks,
            InspectionDate = inspectionDate,
        }, ct);

    public Task<IReadOnlyList<InspectionResponse>> ListInspectionsAsync(Guid busId, CancellationToken ct = default) =>
        QueryInlineAsync<InspectionResponse>(
            $"SELECT {InspectionCols} FROM \"dbo\".\"VehicleInspections\" WHERE \"BusId\" = @busId ORDER BY \"InspectionDate\" DESC",
            new { busId }, ct);

    public Task<FuelLogResponse?> CreateFuelLogAsync(
        Guid tenantId, Guid busId, Guid recordedByUserId, CreateFuelLogRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<FuelLogResponse>("dbo.FuelLog_Create", new
        {
            TenantId = tenantId,
            BusId = busId,
            RecordedByUserId = recordedByUserId,
            r.OdometerKm,
            r.FuelAddedLiters,
        }, ct);

    public Task<IReadOnlyList<FuelLogResponse>> ListFuelLogsAsync(Guid busId, CancellationToken ct = default) =>
        QueryInlineAsync<FuelLogResponse>(
            $"SELECT {FuelLogCols} FROM \"dbo\".\"FuelLogs\" WHERE \"BusId\" = @busId ORDER BY \"RecordedAt\" DESC",
            new { busId }, ct);

    /// True if busId resolves at all under the caller's session (RLS-scoped) — used to give a
    /// manager a real 404 for a cross-tenant/unknown bus id, distinct from the 403
    /// "not assigned" a driver/conductor gets for the same bad id.
    public async Task<bool> BusExistsAsync(Guid busId, CancellationToken ct = default) =>
        (await QueryInlineAsync<int>("SELECT COUNT(1) FROM \"dbo\".\"Buses\" WHERE \"Id\" = @busId", new { busId }, ct)).First() > 0;

    /// Tenant's SchoolAdmin/SchoolOwner/Principal users — targets for the "failed inspection"
    /// notification. Deliberately duplicated from IssueRepository.GetManagerUserIdsAsync (same
    /// query) rather than reaching into the Issues module for a shared helper — Issues is
    /// explicitly off-limits for this feature, and the query is small enough that duplicating it
    /// is lower-risk than adding a cross-module dependency for one lookup.
    public async Task<IReadOnlyList<Guid>> GetManagerUserIdsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<UserIdRow>(@"
SELECT DISTINCT u.""Id""
FROM ""dbo"".""Users"" u
INNER JOIN ""dbo"".""UserRoles"" ur ON ur.""UserId"" = u.""Id""
WHERE u.""TenantId"" = @tenantId AND ur.""Role"" IN (@admin, @owner, @principal)",
            new { tenantId, admin = Policies.SchoolAdmin, owner = Policies.SchoolOwner, principal = Policies.Principal },
            ct);
        return rows.Select(r => r.Id).ToList();
    }
}

public static class VehicleCheckModule
{
    public static IServiceCollection AddVehicleChecksModule(this IServiceCollection services)
    {
        services.AddScoped<VehicleCheckRepository>();
        return services;
    }
}
