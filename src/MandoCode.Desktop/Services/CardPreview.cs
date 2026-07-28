using System.Text;

namespace MandoCode.Desktop.Services;

/// <summary>
/// The quoted lines on a History card. Pure — text in, card line out — so the clipping rules are
/// pinned down by tests instead of being buried in the panel code.
///
/// Two shapes, because the two voices need different treatment. A user turn is short, plain, and
/// already reads like a card line (<see cref="Trim"/> just caps it). An assistant turn is a
/// formatted REPLY — markdown headings, bullets, fenced code, several paragraphs — so quoting its
/// raw text would put "```csharp" on the card. <see cref="ClipReply"/> flattens it back to prose
/// and keeps only the opening sentences.
/// </summary>
public static class CardPreview
{
    /// <summary>Cap for a quoted user turn; shared by the first- and last-message lines so the two
    /// can't drift apart.</summary>
    public const int UserChars = 140;

    /// <summary>Cap for the flattened assistant reply. Tighter than <see cref="UserChars"/>: it's the
    /// third quote on the card, rendered smaller, and it's there to jog recognition rather than to be
    /// read in full.</summary>
    public const int ReplyChars = 160;

    /// <summary>Sentences kept from the start of the reply. The opening of an answer says what the
    /// agent DID; the tail is usually a caveat or an offer to continue.</summary>
    public const int ReplySentences = 2;

    /// <summary>
    /// Words whose trailing period is part of the WORD, not the end of a sentence. Checked by token
    /// rather than by sentence length: "It worked." is a real sentence at ten characters, so any
    /// length threshold big enough to absorb "e.g." would also swallow that.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "cf", "approx", "no", "fig", "al",
        "mr", "mrs", "ms", "dr", "st", "jr", "sr",
    };

    /// <summary>Caps a quoted user turn at <see cref="UserChars"/>. Null (not "") for nothing to
    /// show, so callers can distinguish it from a computed-but-empty line.</summary>
    public static string? Trim(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > UserChars
            ? trimmed[..UserChars].TrimEnd() + "…"
            : trimmed;
    }

    /// <summary>
    /// The agent's last reply as one card line: markdown flattened to prose, then the first
    /// <see cref="ReplySentences"/> sentences, then a hard cap at <see cref="ReplyChars"/>. Returns
    /// null when there's nothing quotable left — a reply that was pure code or a bare table flattens
    /// to nothing, and an absent line reads better than an empty one.
    /// </summary>
    public static string? ClipReply(string? reply)
    {
        var prose = Flatten(reply);
        if (prose.Length == 0) return null;

        var clipped = FirstSentences(prose, ReplySentences);
        return clipped.Length > ReplyChars
            ? clipped[..ReplyChars].TrimEnd() + "…"
            : clipped;
    }

    /// <summary>
    /// Markdown → one line of prose. Fenced code blocks go entirely (they're the noisiest thing a
    /// reply can start with), block prefixes and inline emphasis are stripped, links keep their text,
    /// and every whitespace run collapses so the result never reproduces the reply's own wrapping.
    /// </summary>
    private static string Flatten(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var parts = new List<string>();
        var inFence = false;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();

            // Fence delimiters carry an info string ("```csharp"), so match on the prefix.
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || line.Length == 0) continue;

            line = StripBlockPrefix(line);
            if (line.Length == 0) continue;

            // A table separator ("|---|---|") is punctuation only — it would read as garbage.
            if (line.All(c => c is '|' or '-' or ':' or ' ')) continue;

            parts.Add(StripInline(line));
        }

        return CollapseWhitespace(string.Join(" ", parts));
    }

    /// <summary>Drops the markers that make a line a heading, quote, bullet, or numbered item —
    /// repeatedly, so "> - item" loses both.</summary>
    private static string StripBlockPrefix(string line)
    {
        while (true)
        {
            var before = line;

            if (line.StartsWith('#') || line.StartsWith('>'))
                line = line.TrimStart('#', '>').TrimStart();
            else if (line.Length > 1 && line[0] is '-' or '*' or '+' && char.IsWhiteSpace(line[1]))
                line = line[1..].TrimStart();
            else if (char.IsDigit(line[0]))
            {
                // "12. text" / "12) text" — a numbered item. Anything else starting with a digit
                // (a version, a count) is real prose and must survive.
                var i = 0;
                while (i < line.Length && char.IsDigit(line[i])) i++;
                if (i < line.Length - 1 && line[i] is '.' or ')' && char.IsWhiteSpace(line[i + 1]))
                    line = line[(i + 1)..].TrimStart();
            }

            // A task-list checkbox sits after the bullet, so it's peeled on the next pass.
            if (line.StartsWith("[ ]", StringComparison.Ordinal) || line.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
                line = line[3..].TrimStart();

            if (line == before) return line;
        }
    }

    /// <summary>
    /// Removes inline markup while leaving identifiers intact. Backticks, asterisks and tildes go;
    /// UNDERSCORES STAY, because stripping them would rewrite every <c>snake_case</c> name the reply
    /// mentions — a worse outcome than leaving one stray emphasis marker.
    /// </summary>
    private static string StripInline(string line)
    {
        var sb = new StringBuilder(line.Length);

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is '`' or '*' or '~') continue;

            // "[label](target)" → "label": keep the label, drop the target.
            if (c == '[')
            {
                var close = line.IndexOf(']', i + 1);
                if (close > i && close + 1 < line.Length && line[close + 1] == '(')
                {
                    var end = line.IndexOf(')', close + 2);
                    if (end > close)
                    {
                        sb.Append(StripInline(line[(i + 1)..close]));
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The first <paramref name="count"/> sentences, or the whole thing when it has fewer. A
    /// terminator ends a sentence only when whitespace or end-of-text follows it, so decimals and
    /// file names ("v1.2", "MainWindow.xaml.cs") don't split it; <see cref="Abbreviations"/> catches
    /// the "e.g. " and "etc. " cases that pass that test.
    /// </summary>
    private static string FirstSentences(string text, int count)
    {
        var found = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;

            // Run past "?!" or "..." so the whole cluster ends one sentence, not three.
            var end = i;
            while (end + 1 < text.Length && text[end + 1] is '.' or '!' or '?') end++;

            var atEnd = end + 1 >= text.Length;
            if (!atEnd && !char.IsWhiteSpace(text[end + 1])) { i = end; continue; }
            if (EndsWithAbbreviation(text, i)) { i = end; continue; }

            found++;
            if (found == count || atEnd) return text[..(end + 1)];
            i = end;
        }

        return text;
    }

    /// <summary>True when the word ending at <paramref name="dot"/> is a known abbreviation, so its
    /// period belongs to the word. Only a '.' can do this — "etc!" is someone shouting.</summary>
    private static bool EndsWithAbbreviation(string text, int dot)
    {
        if (text[dot] != '.') return false;

        var start = dot;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;

        // Leading punctuation isn't part of the word: "(e.g." must still read as "e.g".
        var word = text[start..dot].TrimStart('(', '[', '"', '\'', '“', '‘');
        return word.Length > 0 && Abbreviations.Contains(word);
    }

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
