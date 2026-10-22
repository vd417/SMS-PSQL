using Sms.Modules.Academics.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Academics.Data;

public sealed class AcademicPublishRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<PublishSnapshotResponse?> GetPeriodsAsync(CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.AcademicPeriod_Get", null, ct);

    public Task<PublishSnapshotResponse?> UpsertPeriodsAsync(
        Guid tenantId, UpsertPublishSnapshotRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.AcademicPeriod_Upsert", new
        {
            TenantId = tenantId,
            r.DraftJson,
            r.PublishedJson,
            r.DraftSavedAt,
            r.PublishedAt,
        }, ct);

    public Task<PublishSnapshotResponse?> GetClassTestsAsync(CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.ClassTestSchedule_Get", null, ct);

    public Task<PublishSnapshotResponse?> UpsertClassTestsAsync(
        Guid tenantId, UpsertPublishSnapshotRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.ClassTestSchedule_Upsert", new
        {
            TenantId = tenantId,
            r.DraftJson,
            r.PublishedJson,
            r.DraftSavedAt,
            r.PublishedAt,
        }, ct);

    public Task<PublishSnapshotResponse?> GetTimetableAsync(CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.TimetableSchedule_Get", null, ct);

    public Task<PublishSnapshotResponse?> UpsertTimetableAsync(
        Guid tenantId, UpsertPublishSnapshotRequest r, CancellationToken ct = default) =>
        QuerySingleProcAsync<PublishSnapshotResponse>("dbo.TimetableSchedule_Upsert", new
        {
            TenantId = tenantId,
            r.DraftJson,
            r.PublishedJson,
            r.DraftSavedAt,
            r.PublishedAt,
        }, ct);

    public Task AppendTimetableHistoryAsync(
        Guid tenantId, string kind, string snapshotJson, DateTime savedAt, CancellationToken ct = default) =>
        ExecuteProcAsync("dbo.TimetableScheduleHistory_Add", new
        {
            TenantId = tenantId,
            Kind = kind,
            SnapshotJson = snapshotJson,
            SavedAt = savedAt,
        }, ct);

    public Task<IReadOnlyList<TimetableHistoryResponse>> ListTimetableHistoryAsync(CancellationToken ct = default) =>
        QueryProcAsync<TimetableHistoryResponse>("dbo.TimetableScheduleHistory_List", null, ct);
}
