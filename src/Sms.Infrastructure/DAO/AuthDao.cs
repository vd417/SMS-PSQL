using Dapper;
using Sms.Application.Interfaces.DAO;
using Sms.Infrastructure.SQL;
using Sms.Shared.Kernel.Auth;
using Sms.Shared.Kernel.Data;
using Sms.Shared.Kernel.Tenancy;

namespace Sms.Infrastructure.DAO;

public sealed class AuthDao(IDbConnectionFactory factory, ITenantContext tenant) : BaseRepository(factory), IAuthDao
{
    public async Task<UserRecord?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        (await QueryProcAsync<UserRecord>(AuthQueries.GetByEmail, new { Email = email }, ct)).FirstOrDefault();

    public async Task<UserRecord?> GetByPhoneAsync(string phone, CancellationToken ct = default) =>
        (await QueryProcAsync<UserRecord>(AuthQueries.GetByPhone, new { Phone = phone }, ct)).FirstOrDefault();

    public Task<UserRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        QuerySingleProcAsync<UserRecord>(AuthQueries.GetById, new { Id = id }, ct);

    public Task<IReadOnlyList<UserRecord>> ListByEmailAsync(string email, CancellationToken ct = default) =>
        QueryInlineAsync<UserRecord>(
            """
            SELECT "Id", "TenantId", "Email", "StudentId", "Phone", "PasswordHash", "IsPlatform", "Status", "Name", "MustSetPassword", "CreatedAt", "PhotoUrl"
            FROM "dbo"."Users" WHERE "Email" IS NOT NULL
            AND lower(trim("Email")) = lower(trim(@Email))
            ORDER BY CASE WHEN "IsPlatform" THEN 0 ELSE 1 END, "CreatedAt"
            """,
            new { Email = email }, ct);

    public Task<IReadOnlyList<UserRecord>> ListByPhoneAsync(string phone, CancellationToken ct = default) =>
        QueryInlineAsync<UserRecord>(
            """
            SELECT "Id", "TenantId", "Email", "StudentId", "Phone", "PasswordHash", "IsPlatform", "Status", "Name", "MustSetPassword", "CreatedAt", "PhotoUrl"
            FROM "dbo"."Users" WHERE "Phone" = @Phone
            ORDER BY CASE WHEN "IsPlatform" THEN 0 ELSE 1 END, "CreatedAt"
            """,
            new { Phone = phone }, ct);

    public Task<IReadOnlyList<UserRecord>> ListByAdmissionIdAsync(string admissionId, CancellationToken ct = default) =>
        QueryProcAsync<UserRecord>(AuthQueries.ListByAdmissionId, new { AdmissionId = admissionId }, ct);

    public async Task<string?> GetRosterEmailByAdmissionIdAsync(string admissionId, CancellationToken ct = default)
    {
        var roster = await GetRosterByAdmissionIdAsync(admissionId, ct);
        return string.IsNullOrWhiteSpace(roster?.Email) ? null : roster.Email;
    }

    public Task<RosterStudentRecord?> GetRosterByAdmissionIdAsync(string admissionId, CancellationToken ct = default) =>
        QuerySingleProcAsync<RosterStudentRecord>(AuthQueries.GetRosterByAdmissionNo, new { AdmissionId = admissionId }, ct);

    public async Task<RosterStudentRecord?> GetRosterByEmailAsync(string email, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<RosterStudentRecord>(
            """
            SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Email", s."GuardianPhone", s."Status", s."GuardianEmail"
            FROM "dbo"."Students" s
            WHERE s."Email" IS NOT NULL
            AND lower(trim(s."Email")) = lower(trim(@Email))
            AND lower(COALESCE(s."Status", 'active')) NOT IN ('removed', 'inactive', 'left', 'withdrawn')
            ORDER BY s."CreatedAt"
            LIMIT 1
            """,
            new { Email = email }, ct);
        return rows.FirstOrDefault();
    }

    public async Task<RosterStudentRecord?> GetRosterByGuardianEmailAsync(string email, CancellationToken ct = default)
    {
        var rows = await QueryInlineAsync<RosterStudentRecord>(
            """
            SELECT s."Id", s."TenantId", s."AdmissionNo", s."Name", s."Email", s."GuardianPhone", s."Status", s."GuardianEmail"
            FROM "dbo"."Students" s
            WHERE s."GuardianEmail" IS NOT NULL
            AND lower(trim(s."GuardianEmail")) = lower(trim(@Email))
            AND lower(COALESCE(s."Status", 'active')) NOT IN ('removed', 'inactive', 'left', 'withdrawn')
            ORDER BY s."CreatedAt"
            LIMIT 1
            """,
            new { Email = email }, ct);
        return rows.FirstOrDefault();
    }

    public Task<UserRecord?> EnsureStudentLoginAsync(string admissionId, CancellationToken ct = default) =>
        QuerySingleProcAsync<UserRecord>(AuthQueries.EnsureStudentLogin, new { AdmissionId = admissionId }, ct);

    public Task<UserRecord?> EnsureParentLoginAsync(string admissionId, CancellationToken ct = default) =>
        QuerySingleProcAsync<UserRecord>(AuthQueries.EnsureParentLogin, new { AdmissionId = admissionId }, ct);

    public Task<UserRecord?> EnsureStaffLoginAsync(string email, CancellationToken ct = default) =>
        QuerySingleProcAsync<UserRecord>(AuthQueries.EnsureStaffLogin, new { Email = email }, ct);

    public async Task<UserRecord?> GetByEmailAndTenantAsync(string email, Guid tenantId, CancellationToken ct = default) =>
        (await QueryInlineAsync<UserRecord>(
            """
            SELECT "Id", "TenantId", "Email", "StudentId", "Phone", "PasswordHash", "IsPlatform", "Status", "Name", "MustSetPassword", "CreatedAt", "PhotoUrl"
            FROM "dbo"."Users" WHERE "Email" = @Email AND "TenantId" = @TenantId
            """,
            new { Email = email, TenantId = tenantId }, ct)).FirstOrDefault();

    public Task<IReadOnlyList<string>> GetRolesAsync(Guid userId, CancellationToken ct = default) =>
        QueryProcAsync<string>(AuthQueries.GetRoles, new { UserId = userId }, ct);

    public Task SetPasswordAsync(Guid userId, string passwordHash, CancellationToken ct = default) =>
        ExecuteProcAsync(AuthQueries.SetPassword, new { UserId = userId, PasswordHash = passwordHash }, ct);

    public async Task SetPhotoAsync(Guid userId, string? photoUrl, CancellationToken ct = default)
    {
        var user = await GetByIdAsync(userId, ct);
        await ExecuteProcAsync(AuthQueries.SetPhoto, new { UserId = userId, PhotoUrl = photoUrl }, ct);
        await SyncToEmailPeersAsync(user?.Email, userId, async (peerId, _) =>
            await ExecuteProcAsync(AuthQueries.SetPhoto, new { UserId = peerId, PhotoUrl = photoUrl }, ct), ct);
    }

    public async Task SetPhoneAsync(Guid userId, string? phone, CancellationToken ct = default)
    {
        var user = await GetByIdAsync(userId, ct);
        await UpdatePhoneAsync(userId, phone, ct);
        await SyncToEmailPeersAsync(user?.Email, userId,
            async (peerId, _) => await UpdatePhoneAsync(peerId, phone, ct), ct);
    }

    private Task UpdatePhoneAsync(Guid userId, string? phone, CancellationToken ct) =>
        ExecuteInlineUpdateAsync(
            """UPDATE "dbo"."Users" SET "Phone" = @Phone WHERE "Id" = @UserId""",
            new { UserId = userId, Phone = phone }, ct);

    /// Multi-school identities share an email — RLS requires platform context to see all peers.
    private async Task SyncToEmailPeersAsync(
        string? email, Guid sourceUserId, Func<Guid, string?, Task> syncPeer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var prevTenant = tenant.TenantId;
        var prevUser = tenant.UserId;
        var wasPlatform = tenant.IsPlatform;
        tenant.Set(null, null, isPlatform: true);
        try
        {
            foreach (var peer in await ListByEmailAsync(email, ct))
            {
                if (peer.Id == sourceUserId) continue;
                await syncPeer(peer.Id, email);
            }
        }
        finally
        {
            tenant.Set(prevTenant, prevUser, wasPlatform);
        }
    }

    public Task SetEmailAsync(Guid userId, string? email, CancellationToken ct = default) =>
        ExecuteProcAsync(AuthQueries.SetEmail, new { UserId = userId, Email = email }, ct);

    private async Task ExecuteInlineUpdateAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await Factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    public Task OtpInsertAsync(string identifier, string channel, string codeHash,
        DateTime expiresAt, CancellationToken ct = default) =>
        ExecuteProcAsync(AuthQueries.OtpInsert,
            new { Identifier = identifier, Channel = channel, CodeHash = codeHash, ExpiresAt = expiresAt }, ct);

    public async Task<string?> OtpActiveHashAsync(string identifier, CancellationToken ct = default)
    {
        var rows = await QueryProcAsync<OtpRow>(AuthQueries.OtpGetActive, new { Identifier = identifier }, ct);
        return rows.Count == 0 ? null : rows[0].CodeHash;
    }

    public async Task<bool> OtpConsumeAsync(string identifier, string codeHash, CancellationToken ct = default) =>
        await QuerySingleProcAsync<int>(AuthQueries.OtpConsume, new { Identifier = identifier, CodeHash = codeHash }, ct) > 0;

    public Task OtpConsumeAllAsync(string identifier, CancellationToken ct = default) =>
        ExecuteProcAsync(AuthQueries.OtpConsumeAll, new { Identifier = identifier }, ct);

    private sealed record OtpRow(Guid Id, string CodeHash);
}
