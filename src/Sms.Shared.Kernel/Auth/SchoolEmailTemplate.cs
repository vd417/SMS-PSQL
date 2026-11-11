using System.Net;
using System.Text.RegularExpressions;

namespace Sms.Shared.Kernel.Auth;

public static partial class SchoolEmailTemplate
{
    public static readonly string[] Placeholders =
    [
        "school_name", "student_name", "parent_name", "admission_number", "receipt_number",
        "amount_paid", "pending_amount", "due_date", "payment_date", "payment_mode", "academic_year",
    ];

    public static string Render(string? template, IReadOnlyDictionary<string, string>? values)
    {
        var source = Sanitize(template ?? "");
        if (source.Length == 0) return "";
        return Placeholder().Replace(source, match =>
        {
            var key = match.Groups[1].Value;
            if (!Placeholders.Contains(key)) return "";
            var raw = values is not null && values.TryGetValue(key, out var value) ? value : "";
            return WebUtility.HtmlEncode(raw ?? "");
        });
    }

    public static string Sanitize(string html)
    {
        var text = Script().Replace(html, "");
        text = Frame().Replace(text, "");
        text = Handler().Replace(text, "");
        return text;
    }

    [GeneratedRegex(@"\{\{\s*([a-z_]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Script();

    [GeneratedRegex(@"<iframe\b[^>]*>.*?</iframe>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Frame();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Handler();
}
