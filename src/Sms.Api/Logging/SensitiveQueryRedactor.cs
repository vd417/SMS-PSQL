using System.Text.RegularExpressions;

namespace Sms.Api.Logging;

/// Masks the values of credential-bearing query-string parameters in any text that may contain a
/// URL or query string (e.g. "?access_token=eyJ..." -> "?access_token=[REDACTED]"). SignalR clients
/// in browsers can only send their JWT as ?access_token=, and ASP.NET Core's hosting diagnostics
/// ("Request starting/finished ...") log the raw query string, so without this every hub
/// connection would write a reusable bearer token to the journal. Parameter names are matched
/// case-insensitively and after URL-decoding; every other parameter is left untouched.
public static partial class SensitiveQueryRedactor
{
    public const string Mask = "[REDACTED]";

    /// Add new names here, lower-case. Keep it to parameters that carry credentials.
    public static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "refresh_token", "id_token", "token", "password", "secret", "client_secret",
        "api_key", "apikey",
    };

    // A key=value pair that starts the text or follows ?, & or ;. The value runs to the next
    // separator, fragment or whitespace (log lines put " - " after the URL).
    [GeneratedRegex(@"(?<=^|[?&;])(?<key>[^=&;#?\s]+)=(?<value>[^&;#\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Pair();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('=')) return text;
        return Pair().Replace(text, m =>
            IsSensitive(m.Groups["key"].Value) && m.Groups["value"].Length > 0
                ? $"{m.Groups["key"].Value}={Mask}"
                : m.Value);
    }

    private static bool IsSensitive(string key)
    {
        if (SensitiveKeys.Contains(key)) return true;
        try { return SensitiveKeys.Contains(Uri.UnescapeDataString(key.Replace('+', ' '))); }
        catch (UriFormatException) { return false; }
    }
}
