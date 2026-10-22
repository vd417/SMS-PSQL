namespace Sms.Modules.Academics.Contracts;

public sealed record PublishSnapshotResponse(
    Guid Id,
    Guid TenantId,
    string? DraftJson,
    string? PublishedJson,
    DateTime? DraftSavedAt,
    DateTime? PublishedAt);

public sealed record UpsertPublishSnapshotRequest(
    string? DraftJson,
    string? PublishedJson,
    DateTime? DraftSavedAt,
    DateTime? PublishedAt);

public sealed record TimetableHistoryResponse(
    Guid Id,
    string Kind,
    string? SnapshotJson,
    DateTime SavedAt);
