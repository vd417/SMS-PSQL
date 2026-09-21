using System.Data.Common;
using System.Text.Json;
using Dapper;

namespace Sms.Shared.Kernel.Audit;

/// <summary>
/// A generic, insert-only audit record. No application code exposes update/delete for AuditLogs —
/// once written inside a transaction, a row is immutable and permanent.
/// </summary>
public sealed record AuditEntry(
    Guid TenantId,
    Guid? ActorUserId,
    string Action,
    string Module,
    string EntityType,
    string EntityId,
    object? BeforeData = null,
    object? AfterData = null);

/// <summary>
/// Writes an AuditLogs row using the caller's own open connection and transaction, so the audit
/// write commits or rolls back atomically with whatever business change it records. Callers are
/// expected to already be inside a transaction — this type never opens or manages one itself.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(DbConnection conn, DbTransaction tx, AuditEntry entry, CancellationToken ct = default);
}

public sealed class AuditLogger : IAuditLogger
{
    public Task LogAsync(DbConnection conn, DbTransaction tx, AuditEntry entry, CancellationToken ct = default) =>
        conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO "dbo"."AuditLogs" ("TenantId", "ActorUserId", "Action", "Module", "EntityType", "EntityId", "BeforeData", "AfterData")
            VALUES (@TenantId, @ActorUserId, @Action, @Module, @EntityType, @EntityId, @BeforeData, @AfterData)
            """,
            new
            {
                entry.TenantId,
                entry.ActorUserId,
                entry.Action,
                entry.Module,
                entry.EntityType,
                entry.EntityId,
                BeforeData = entry.BeforeData is null ? null : JsonSerializer.Serialize(entry.BeforeData),
                AfterData = entry.AfterData is null ? null : JsonSerializer.Serialize(entry.AfterData),
            },
            tx,
            cancellationToken: ct));
}
