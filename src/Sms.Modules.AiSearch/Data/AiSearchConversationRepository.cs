using Sms.Shared.Kernel.Data;

namespace Sms.Modules.AiSearch.Data;

public sealed record AiSearchConversationRow(
    Guid ConversationId, Guid TenantId, Guid UserId, Guid? ResolvedEntityId, string? ResolvedEntityType,
    string? LanguageOverride, string? PendingCandidates, string? LastIntent, DateTime CreatedAt, DateTime ExpiresAt);

public sealed class AiSearchConversationRepository(IDbConnectionFactory factory) : BaseRepository(factory)
{
    public async Task<AiSearchConversationRow?> FindAsync(
        Guid conversationId, Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<AiSearchConversationRow>(
            @"SELECT ""ConversationId"", ""TenantId"", ""UserId"", ""ResolvedEntityId"", ""ResolvedEntityType"",
                     ""LanguageOverride"", ""PendingCandidates"", ""LastIntent"", ""CreatedAt"", ""ExpiresAt""
              FROM ""dbo"".""AiSearchConversation""
              WHERE ""ConversationId"" = @conversationId AND ""TenantId"" = @tenantId AND ""UserId"" = @userId",
            new { conversationId, tenantId, userId }, ct);
        return rows.FirstOrDefault();
    }

    public Task UpsertAsync(AiSearchConversationRow row, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            @"INSERT INTO ""dbo"".""AiSearchConversation""
                  (""ConversationId"", ""TenantId"", ""UserId"", ""ResolvedEntityId"", ""ResolvedEntityType"",
                   ""LanguageOverride"", ""PendingCandidates"", ""LastIntent"", ""CreatedAt"", ""ExpiresAt"")
                  VALUES (@ConversationId, @TenantId, @UserId, @ResolvedEntityId, @ResolvedEntityType,
                          @LanguageOverride, @PendingCandidates, @LastIntent, @CreatedAt, @ExpiresAt)
              ON CONFLICT (""ConversationId"") DO UPDATE SET
                  ""ResolvedEntityId"" = @ResolvedEntityId, ""ResolvedEntityType"" = @ResolvedEntityType,
                  ""LanguageOverride"" = @LanguageOverride, ""PendingCandidates"" = @PendingCandidates,
                  ""LastIntent"" = @LastIntent, ""ExpiresAt"" = @ExpiresAt;",
            row, ct);

    public Task DeleteAsync(Guid conversationId, CancellationToken ct = default) =>
        ExecuteInlineAsync(
            "DELETE FROM \"dbo\".\"AiSearchConversation\" WHERE \"ConversationId\" = @conversationId",
            new { conversationId }, ct);
}
