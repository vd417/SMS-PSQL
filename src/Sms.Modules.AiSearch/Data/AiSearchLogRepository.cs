using Sms.Shared.Kernel.Data;

namespace Sms.Modules.AiSearch.Data;

public sealed record AiSearchLogEntry(
    Guid TenantId, Guid UserId, string Role, string Question,
    string? DetectedLanguage, string? DetectedIntent, int ResultCount, bool Success, DateTime At);

public sealed class AiSearchLogRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public Task<int> InsertAsync(AiSearchLogEntry entry, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            @"INSERT INTO ""dbo"".""AiSearchLog""
                (""Id"", ""TenantId"", ""UserId"", ""Role"", ""Question"", ""DetectedLanguage"", ""DetectedIntent"", ""ResultCount"", ""Success"", ""At"")
              VALUES
                (gen_random_uuid(), @TenantId, @UserId, @Role, @Question, @DetectedLanguage, @DetectedIntent, @ResultCount, @Success, @At)",
            entry, ct);
}
