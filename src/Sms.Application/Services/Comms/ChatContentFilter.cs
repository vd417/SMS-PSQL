using System.Globalization;
using System.Text;
using Sms.Modules.Comms;

namespace Sms.Application.Services.Comms;

public sealed record ChatContentCheck(bool Blocked, string? MatchedWord);

public interface IChatContentFilter
{
    /// Returns Blocked=true (with the matched banned word, for server-side logging) when the text
    /// contains a banned word/phrase. Empty/whitespace text is always allowed.
    Task<ChatContentCheck> CheckAsync(string? text, CancellationToken ct = default);
}

/// <summary>
/// Chat profanity filter backed by dbo.ChatBannedWords (global + tenant rows; RLS scopes them).
/// Single words match EXACTLY against message tokens, so normal words stay safe — "class" is not
/// flagged by "ass", "manager" not by "anger", "island" not by "land". Phrases match as a collapsed
/// (spaces removed) substring. A small symbol de-leet (@->a, $->s, !->i and *,# stripped) catches
/// common evasions like a$$, f**k, d!ck; digit leet is intentionally NOT applied (the explicit leet
/// variants such as s3x/n00d are already in the list, and mapping digits would flag "18"/"21").
/// </summary>
public sealed class ChatContentFilter(CommsRepository repo) : IChatContentFilter
{
    public async Task<ChatContentCheck> CheckAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ChatContentCheck(false, null);
        var words = await repo.ListBannedWordsAsync(ct);
        return Match(words, text);
    }

    /// <summary>Pure matcher (no DB) — exposed for unit testing.</summary>
    public static ChatContentCheck Match(IReadOnlyCollection<ChatBannedWord> words, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || words.Count == 0)
            return new ChatContentCheck(false, null);

        var tokens = TokenSet(text);
        var collapsed = Collapse(text);

        foreach (var w in words)
        {
            var norm = Collapse(w.Word);
            if (norm.Length == 0) continue;

            if (string.Equals(w.Kind, "phrase", StringComparison.OrdinalIgnoreCase))
            {
                if (norm.Length >= 4 && collapsed.Contains(norm, StringComparison.Ordinal))
                    return new ChatContentCheck(true, w.Word);
            }
            else if (tokens.Contains(norm))
            {
                return new ChatContentCheck(true, w.Word);
            }
        }
        return new ChatContentCheck(false, null);
    }

    // '@'/'$'/'!' are read as letters; '*'/'#' are stripped so they join rather than break a word
    // ("f**k" -> "fk"). null = strip-and-join.
    private static char? MapLeet(char ch) => ch switch
    {
        '@' => 'a',
        '$' => 's',
        '!' => 'i',
        '*' or '#' => null,
        _ => ch,
    };

    // A word character: letters/digits, plus combining marks so Indic matras (e.g. the vowel signs
    // in "गाली") stay attached to their consonant instead of splitting the word apart.
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    // Lowercased, de-leeted, word characters only, spaces removed — one string (phrase substring).
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s.ToLowerInvariant())
        {
            if (MapLeet(raw) is { } c && IsWordChar(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    // De-leeted word tokens, split on any non-letter/digit (Devanagari counts as letters). Used for
    // exact single-word matching.
    private static HashSet<string> TokenSet(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var raw in s.ToLowerInvariant())
        {
            var mapped = MapLeet(raw);
            if (mapped is null) continue;            // stripped: join, no split
            if (IsWordChar(mapped.Value)) { sb.Append(mapped.Value); continue; }
            if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }   // separator: end token
        }
        if (sb.Length > 0) set.Add(sb.ToString());
        return set;
    }
}
