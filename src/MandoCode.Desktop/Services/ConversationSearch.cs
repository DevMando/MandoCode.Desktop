using System.Text;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Full-text matching over an archived conversation's plain-text log, and the snippet that explains
/// a hit. Deliberately pure — text in, match out — so it's unit tested directly; the caller owns the
/// file reads (<see cref="ConversationLog"/>) and the caching (<see cref="ConversationTextCache"/>).
/// </summary>
public static class ConversationSearch
{
    /// <summary>Characters of context kept either side of a hit. Sized for the History card, which is
    /// two lines wide in a docked panel.</summary>
    public const int SnippetRadius = 60;

    /// <summary>Queries shorter than this aren't worth reading every log for — a single character
    /// matches nearly every conversation, so the result wouldn't narrow anything.</summary>
    public const int MinQueryLength = 2;

    /// <summary>One searchable blob per conversation: the turn texts joined by newlines. Roles are
    /// dropped on purpose — searching for "a" or "u" shouldn't match every turn marker.</summary>
    public static string Flatten(IEnumerable<ConversationTurn> turns) =>
        string.Join("\n", turns.Select(t => t.T));

    /// <summary>
    /// A one-line window around the first occurrence of <paramref name="query"/>, ellipsised at
    /// whichever end was truncated, or null when there's no match. Newlines and whitespace runs
    /// collapse to single spaces so a snippet always renders as one tidy line on the card rather
    /// than reproducing the log's own wrapping.
    /// </summary>
    public static string? Snippet(string? text, string? query, int radius = SnippetRadius)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) return null;

        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var start = Math.Max(0, at - radius);
        var end = Math.Min(text.Length, at + query.Length + radius);

        var window = CollapseWhitespace(text[start..end]);
        if (window.Length == 0) return null;

        // Ellipses mark real truncation only, so a short conversation reads as a complete quote.
        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        return prefix + window + suffix;
    }

    /// <summary>True when <paramref name="query"/> is long enough to justify scanning the logs.</summary>
    public static bool IsSearchable(string? query) =>
        !string.IsNullOrWhiteSpace(query) && query.Trim().Length >= MinQueryLength;

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;   // never lead with a space
                continue;
            }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }

        return sb.ToString();
    }
}
