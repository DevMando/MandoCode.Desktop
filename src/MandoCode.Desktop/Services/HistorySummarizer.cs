using System.Text;
using Microsoft.SemanticKernel;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Flattens a chat history into a plain-text transcript, fed by the public
/// <c>AIService.GetHistoryAsync()</c>. Snapshots buffer this <see cref="Full"/> dump and hand it to
/// <see cref="SnapshotEnhancer"/> to summarize — the LLM does the recap, so nothing here truncates.
///
/// (The deterministic per-line/overall truncation this once did — a port of the harness's compaction
/// summary — was dropped when snapshots moved to LLM summaries: it kept the oldest turns and cut the
/// most recent, which is backwards for a resumption recap.)
/// </summary>
public static class HistorySummarizer
{
    private const string Empty = "(no prior activity captured)";

    /// <summary>True if there is anything worth snapshotting beyond the system prompt at index 0.</summary>
    public static bool HasContent(IReadOnlyList<ChatMessageContent> history, int startIndex = 1)
    {
        for (int i = Math.Max(0, startIndex); i < history.Count; i++)
            if (!string.IsNullOrEmpty(FormatMessage(history[i], int.MaxValue))) return true;
        return false;
    }

    /// <summary>Full untruncated dump — the text handed to the summarizer.</summary>
    public static string Full(IReadOnlyList<ChatMessageContent> history, int startIndex = 1)
        => Build(history, startIndex, lineMax: int.MaxValue, maxChars: int.MaxValue);

    private static string Build(IReadOnlyList<ChatMessageContent> history, int startIndex, int lineMax, int maxChars)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(0, startIndex); i < history.Count; i++)
        {
            var line = FormatMessage(history[i], lineMax);
            if (string.IsNullOrEmpty(line)) continue;

            sb.Append('[').Append(history[i].Role.Label).Append("] ").AppendLine(line);
            if (sb.Length > maxChars)
            {
                sb.AppendLine("... (older entries truncated)");
                break;
            }
        }
        return sb.Length == 0 ? Empty : sb.ToString().TrimEnd();
    }

    /// <summary>One-line recap of a single message; falls back to function calls/results when the
    /// text content is empty (a tool turn).</summary>
    private static string FormatMessage(ChatMessageContent msg, int lineMax)
    {
        var content = msg.Content?.Trim();
        if (!string.IsNullOrEmpty(content)) return Cap(content, lineMax);

        if (msg.Items == null || msg.Items.Count == 0) return "";

        var parts = new List<string>();
        foreach (var item in msg.Items)
        {
            switch (item)
            {
                case FunctionCallContent fc:
                {
                    var args = fc.Arguments is { Count: > 0 }
                        ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 40)}"))
                        : "";
                    parts.Add($"called {fc.FunctionName}({args})");
                    break;
                }
                case FunctionResultContent fr:
                    parts.Add($"{fr.FunctionName} → {Truncate(fr.Result?.ToString(), 80)}");
                    break;
                case TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                    parts.Add(tc.Text.Trim());
                    break;
            }
        }
        return parts.Count == 0 ? "" : Cap(string.Join("; ", parts), lineMax);
    }

    private static string Cap(string s, int max) => s.Length > max ? s[..max] + "..." : s;

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
