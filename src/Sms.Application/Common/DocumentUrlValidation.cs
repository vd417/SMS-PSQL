using Sms.Shared.Kernel.Results;

namespace Sms.Application.Common;

/// <summary>Rules for ID-proof / document fields stored as a data URL or http(s)
/// URL directly on a row (no blob storage). Permits PDF in addition to images,
/// with a larger cap than <see cref="ImageUrlValidation"/> (which rejects PDFs
/// and caps at ~300KB). Used by Tenants.IdProofUrl.</summary>
public static class DocumentUrlValidation
{
    // ~2.5 MB of base64. Matches the TeamDocuments 2.5MB document limit.
    private const int MaxChars = 3_500_000;

    public static Error? Validate(string? url)
    {
        if (url is { Length: > MaxChars })
            return new Error("invalid_request", "ID proof is too large (max ~2.5MB)");
        if (url is { Length: > 0 } &&
            !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("data:application/pdf", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Error("invalid_request", "ID proof must be an image/PDF data URL or http(s) URL");
        return null;
    }

    /// <summary>Normalizes a validated value for storage: blank/whitespace-only
    /// becomes null (clears the field), everything else is trimmed.</summary>
    public static string? Normalize(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
