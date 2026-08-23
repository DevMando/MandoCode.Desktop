using System.Text;
using Microsoft.Extensions.AI;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Flattens a chat history into a plain-text transcript, fed by the public
/// <c>AIService.GetHistoryAsync()</c>. Snapshots buffer this <see cref="Full"/> dump and hand it to
/// <see cref="SnapshotEnhancer"/> to summarize — the LLM does the recap, so nothing here truncates.
///
/// (The deterministic per-line/overall truncation this once did — a port of the harness's compaction
/// summary — was dropped when snapshots moved to LLM summaries: it kept the oldest turns and cut the
/// most recent, which is backwards for a resumption recap.)
///
/// Messages are <see cref="ChatMessage"/> (Microsoft.Extensions.AI) since the engine moved off
/// Semantic Kernel. Tool activity lives in <see cref="ChatMessage.Contents"/> alongside any text, so
/// walking Contents — not the <c>Text</c> convenience view — is what surfaces a tool turn.
/// </summary>
public static class HistorySummarizer
{
    private const string Empty = "(no prior activity captured)";

    /// <summary>True if there is anything worth snapshotting beyond the system prompt at index 0.</summary>
    public static bool HasContent(IReadOnlyList<ChatMessage> history, int startIndex = 1)
    {
        var names = MapCallIdsToNames(history, startIndex);
        for (int i = Math.Max(0, startIndex); i < history.Count; i++)
            if (!string.IsNullOrEmpty(FormatMessage(history[i], names, int.MaxValue))) return true;
        return false;
    }

    /// <summary>Full untruncated dump — the text handed to the summarizer.</summary>
    public static string Full(IReadOnlyList<ChatMessage> history, int startIndex = 1)
        => Build(history, startIndex, lineMax: int.MaxValue, maxChars: int.MaxValue);

    private static string Build(IReadOnlyList<ChatMessage> history, int startIndex, int lineMax, int maxChars)
    {
        var names = MapCallIdsToNames(history, startIndex);
        var sb = new StringBuilder();
        for (int i = Math.Max(0, startIndex); i < history.Count; i++)
        {
            var line = FormatMessage(history[i], names, lineMax);
            if (string.IsNullOrEmpty(line)) continue;

            sb.Append('[').Append(history[i].Role.Value).Append("] ").AppendLine(line);
            if (sb.Length > maxChars)
            {
                sb.AppendLine("... (older entries truncated)");
                break;
            }
        }
        return sb.Length == 0 ? Empty : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Call id → function name, built once per walk. Unlike Semantic Kernel's, MEAI's
    /// <see cref="FunctionResultContent"/> carries no function name — only the call id it shares
    /// with its matching <see cref="FunctionCallContent"/> — so a result line would otherwise read
    /// as a bare id instead of "read_file → ...".
    /// </summary>
    private static Dictionary<string, string> MapCallIdsToNames(IReadOnlyList<ChatMessage> history, int startIndex)
    {
        var map = new Dictionary<string, string>();
        for (int i = Math.Max(0, startIndex); i < history.Count; i++)
            foreach (var item in history[i].Contents)
                if (item is FunctionCallContent fc) map[fc.CallId] = fc.Name;
        return map;
    }

    /// <summary>One-line recap of a single message; falls back to function calls/results when the
    /// text content is empty (a tool turn).</summary>
    private static string FormatMessage(ChatMessage msg, Dictionary<string, string> callIdToName, int lineMax)
    {
        var content = msg.Text?.Trim();
        if (!string.IsNullOrEmpty(content)) return Cap(content, lineMax);

        if (msg.Contents.Count == 0) return "";

        var parts = new List<string>();
        foreach (var item in msg.Contents)
        {
            switch (item)
            {
                case FunctionCallContent fc:
                {
                    var args = fc.Arguments is { Count: > 0 }
                        ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 40)}"))
                        : "";
                    parts.Add($"called {fc.Name}({args})");
                    break;
                }
                case FunctionResultContent fr:
                {
                    var name = callIdToName.TryGetValue(fr.CallId, out var n) ? n : fr.CallId;
                    parts.Add($"{name} → {Truncate(fr.Result?.ToString(), 80)}");
                    break;
                }
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
