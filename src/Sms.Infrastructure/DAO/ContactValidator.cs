using Npgsql;
using Sms.Application.Interfaces.DAO;
using Sms.Shared.Kernel.Data;

namespace Sms.Infrastructure.DAO;

/// C# boundary over dbo.contact_claims_sync (migration 0011). The SQL function raises the
/// SMSDC sentinel ("contact_conflict:email" | "contact_conflict:phone") for an expected
/// uniqueness conflict; this maps exactly that sentinel to a normal <see cref="ContactSyncResult"/>.
/// The PostgresException catch lives only here, at the single call site of the function. Any other
/// database error (including an SMSDC with an unrecognised message) propagates unchanged.
public sealed class ContactValidator(IDbConnectionFactory factory) : BaseRepository(factory), IContactValidator
{
    // SQLSTATE and message text are the exact contract defined by 0011_contact_claims_sync_fn.sql.
    private const string ConflictSqlState = "SMSDC";
    private const string EmailConflictMessage = "contact_conflict:email";
    private const string PhoneConflictMessage = "contact_conflict:phone";

    public async Task<ContactSyncResult> SyncAsync(
        Guid tenantId, string ownerType, string ownerId, Guid? personId,
        string? email, string? phone, CancellationToken ct = default)
    {
        try
        {
            // Explicit casts on the optional args so a NULL binds to a concrete Postgres type
            // instead of "unknown", which would otherwise fail function resolution.
            await ExecuteInlineAsync(
                """
                SELECT dbo.contact_claims_sync(
                    @Tenant::uuid, @OwnerType::text, @OwnerId::text, @Person::uuid, @Email::text, @Phone::text)
                """,
                new
                {
                    Tenant = tenantId,
                    OwnerType = ownerType,
                    OwnerId = ownerId,
                    Person = personId,
                    Email = email,
                    Phone = phone,
                },
                ct);
            return ContactSyncResult.Valid;
        }
        catch (PostgresException ex) when (ex.SqlState == ConflictSqlState && ex.MessageText == EmailConflictMessage)
        {
            return ContactSyncResult.Conflict(ContactConflictKind.Email);
        }
        catch (PostgresException ex) when (ex.SqlState == ConflictSqlState && ex.MessageText == PhoneConflictMessage)
        {
            return ContactSyncResult.Conflict(ContactConflictKind.Phone);
        }
        // Any other PostgresException — including an SMSDC with an unrecognised message — is
        // unexpected and propagates through the existing error handling, never becoming a conflict.
    }
}
