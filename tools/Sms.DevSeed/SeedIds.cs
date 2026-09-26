using System.Security.Cryptography;
using System.Text;

namespace Sms.DevSeed;

/// Deterministic ids: the same key maps to the same Guid on every run, which is what makes
/// INSERT ... ON CONFLICT DO NOTHING idempotent. Never change the prefix: it would orphan seeded rows.
public static class SeedIds
{
    public static Guid Of(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("sms-devseed/v1/" + key));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version nibble (name-based)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }
}
