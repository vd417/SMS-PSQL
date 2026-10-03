namespace Sms.Application.Interfaces.DAO;

/// Which contact field a uniqueness conflict is about, mirroring the SQL contract's
/// "contact_conflict:&lt;kind&gt;" sentinel raised by dbo.contact_claims_sync.
public enum ContactConflictKind
{
    Email,
    Phone,
}

/// Outcome of syncing an owner's contact claims. An expected uniqueness conflict is a normal
/// result value (never an exception): IsValid is false and ConflictKind names the offending field.
public readonly record struct ContactSyncResult(bool IsValid, ContactConflictKind? ConflictKind)
{
    public static readonly ContactSyncResult Valid = new(true, null);

    public static ContactSyncResult Conflict(ContactConflictKind kind) => new(false, kind);
}

/// Maintains the dbo.ContactClaims uniqueness ledger for a single owner (via
/// dbo.contact_claims_sync) and reports an expected email/phone conflict as a result value.
public interface IContactValidator
{
    /// Claims the given normalized email/phone for this owner in this tenant. Returns
    /// <see cref="ContactSyncResult.Valid"/> on success, or a conflict result when the value is
    /// already held by a different person. Unexpected database errors propagate.
    Task<ContactSyncResult> SyncAsync(
        Guid tenantId,
        string ownerType,
        string ownerId,
        Guid? personId,
        string? email,
        string? phone,
        CancellationToken ct = default);
}
