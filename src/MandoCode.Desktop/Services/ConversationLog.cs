using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>One turn in the plain-text conversation log ("u" = user, "a" = assistant).</summary>
public sealed record ConversationTurn(string R, string T);

/// <summary>
/// Tier-3 companion to <see cref="TranscriptJournal"/>: a per-session plain-TEXT log of
/// user/assistant turns (no HTML, no tool chrome), used to re-brief the model after a
/// restart. Same contract as the journal: append-on-write (crash-safe), write-side cap
/// (bounded even if the app never restarts), best-effort everywhere.
/// </summary>
public static class ConversationLog
{
    private const int MaxTurns = 200;
    private const int TrimSlack = 64;
    /// <summary>Per-turn size cap — one giant paste must not dominate the re-brief.</summary>
    private const int MaxTurnChars = 4000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "conversations");

    private static string PathFor(string key) => Path.Combine(Folder, key + ".jsonl");

    public static void Append(string key, string role, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > MaxTurnChars) text = text[..MaxTurnChars] + "… [truncated]";

            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = PathFor(key);

                if (!Counts.TryGetValue(key, out var count))
                    count = File.Exists(path) ? File.ReadLines(path).Count() : 0;

                File.AppendAllText(path, JsonSerializer.Serialize(new ConversationTurn(role, text)) + "\n");
                count++;

                if (count > MaxTurns + TrimSlack)
                {
                    var tail = File.ReadAllLines(path).Where(l => l.Length > 0).TakeLast(MaxTurns).ToArray();
                    File.WriteAllLines(path, tail);
                    count = tail.Length;
                }

                Counts[key] = count;
            }
        }
        catch { }
    }

    /// <summary>Turns oldest-first; empty on any failure.</summary>
    public static IReadOnlyList<ConversationTurn> Load(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return Array.Empty<ConversationTurn>();

            string[] lines;
            lock (Gate) lines = File.ReadAllLines(path);

            var turns = new List<ConversationTurn>();
            foreach (var line in lines.Where(l => l.Length > 0))
            {
                try
                {
                    var turn = JsonSerializer.Deserialize<ConversationTurn>(line);
                    if (turn != null && !string.IsNullOrEmpty(turn.T)) turns.Add(turn);
                }
                catch { /* torn line — skip */ }
            }
            return turns;
        }
        catch
        {
            return Array.Empty<ConversationTurn>();
        }
    }

    /// <summary>Last write time of a session's log, or null when there isn't one. Used by
    /// <see cref="ConversationTextCache"/> to revalidate cached text, so a session that was reopened
    /// and closed again is re-read instead of searched against a stale copy.</summary>
    public static DateTime? LastWriteUtc(string key)
    {
        try
        {
            var path = PathFor(key);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch { return null; }
    }

    public static void Delete(string key)
    {
        try
        {
            lock (Gate)
            {
                File.Delete(PathFor(key));
                Counts.Remove(key);
            }
        }
        catch { }
    }

    public static void Sweep(IEnumerable<string> liveKeys)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var keep = new HashSet<string>(liveKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Folder, "*.jsonl"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }
}
