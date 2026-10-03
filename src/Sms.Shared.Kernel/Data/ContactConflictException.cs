using System.Data.Common;

namespace Sms.Shared.Kernel.Data;

/// Thrown at the repository boundary when a create/update proc rejects a write because the owner's
/// email or phone is already claimed by a different person in the tenant. The proc raises the
/// SMSDC sentinel ("contact_conflict:&lt;kind&gt;") from dbo.contact_claims_sync; BaseRepository maps
/// that one SQLSTATE to this exception so services can surface the existing friendly `conflict`
/// (HTTP 409) without reaching into Npgsql. Any other database error propagates unchanged.
public sealed class ContactConflictException : Exception
{
    /// The SQLSTATE raised by dbo.contact_claims_sync for a contact-uniqueness conflict.
    public const string SqlState = "SMSDC";

    /// "email" or "phone" — which field collided, parsed from the sentinel message.
    public string Kind { get; }

    public bool IsPhone => string.Equals(Kind, "phone", StringComparison.OrdinalIgnoreCase);

    public ContactConflictException(string kind, Exception? inner = null)
        : base($"contact_conflict:{kind}", inner) => Kind = kind;

    /// Maps the SMSDC sentinel's message ("contact_conflict:email" | "contact_conflict:phone") to a
    /// typed exception. Defaults to the email kind for any unrecognised shape of the sentinel.
    public static ContactConflictException FromSentinel(DbException ex)
    {
        var kind = ex.Message.Contains("phone", StringComparison.OrdinalIgnoreCase) ? "phone" : "email";
        return new ContactConflictException(kind, ex);
    }
}
