using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using Sms.Modules.Sis.Contracts;
using Sms.Shared.Kernel.Data;

namespace Sms.Modules.Sis.Data;

public sealed class BulkImportRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<BulkImportBatchResponse?> GetExistingResultAsync(
        Guid tenantId, Guid importId, int batchIndex, CancellationToken ct = default)
    {
        var row = (await QueryInlineAsync<string?>(
            "SELECT \"ResultJson\" FROM \"dbo\".\"BulkImportBatches\" WHERE \"TenantId\" = @tenantId AND \"ImportId\" = @importId AND \"BatchIndex\" = @batchIndex",
            new { tenantId, importId, batchIndex }, ct)).FirstOrDefault();
        return row is null ? null : JsonSerializer.Deserialize<BulkImportBatchResponse>(row);
    }

    /// Atomically claims this (tenantId, importId, batchIndex) before any row processing starts,
    /// by inserting a placeholder row (ResultJson = NULL) that the unique index protects. True =
    /// this call won the claim and must process the batch; false = someone else already claimed
    /// or completed it (caller should treat this as "not my batch to process").
    public async Task<bool> TryClaimBatchAsync(
        Guid tenantId, Guid importId, int batchIndex, CancellationToken ct = default)
    {
        await using var conn = await Factory.OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO "dbo"."BulkImportBatches" ("Id", "TenantId", "ImportId", "BatchIndex", "ResultJson", "CreatedAt")
                VALUES (@id, @tenantId, @importId, @batchIndex, NULL, now())
                """,
                new { id = Guid.NewGuid(), tenantId, importId, batchIndex },
                commandType: CommandType.Text, cancellationToken: ct));
            return true;
        }
        catch (PostgresException sqlEx) when (sqlEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    /// Fills in the result on the row this caller already claimed via TryClaimBatchAsync.
    public Task CompleteBatchAsync(
        Guid tenantId, Guid importId, int batchIndex, BulkImportBatchResponse result, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            """
            UPDATE "dbo"."BulkImportBatches" SET "ResultJson" = @resultJson
            WHERE "TenantId" = @tenantId AND "ImportId" = @importId AND "BatchIndex" = @batchIndex
            """,
            new { tenantId, importId, batchIndex, resultJson = JsonSerializer.Serialize(result) }, ct);
}
