using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Append-only on-disk journal of each session's transcript HTML blocks — tier 2 of session
/// persistence. One JSONL file per session persist-key: every block TranscriptWriter emits
/// is appended the moment it happens (crash-safe by construction — there is no save-on-exit
/// to miss), and replayed into a fresh WebView on restore. Best-effort everywhere: a failed
/// journal write or read must never break the live chat.
/// </summary>
public static class TranscriptJournal
{
    /// <summary>Replay/retention cap: the newest N blocks are kept.</summary>
    private const int MaxBlocks = 1000;

    /// <summary>Write-side trim slack. Trimming rewrites the whole file, so it runs once per
    /// ~SLACK appends instead of on every one. Enforced during Append — an app that never
    /// restarts (and therefore never Loads) must still have bounded journals.</summary>
    private const int TrimSlack = 256;

    private static readonly object Gate = new();

    /// <summary>Line counts per key, maintained so Append knows when to trim without
    /// re-counting the file each time. Lazily initialized on first touch. Guarded by Gate.</summary>
    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "transcripts");

    private static string PathFor(string key) => Path.Combine(Folder, key + ".jsonl");

    public static void Append(string key, string html)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var path = PathFor(key);

                if (!Counts.TryGetValue(key, out var count))
                    count = File.Exists(path) ? File.ReadLines(path).Count() : 0;

                File.AppendAllText(path, JsonSerializer.Serialize(html) + "\n");
                count++;

                if (count > MaxBlocks + TrimSlack)
                {
                    var tail = File.ReadAllLines(path)
                        .Where(l => l.Length > 0)
                        .TakeLast(MaxBlocks).ToArray();
                    File.WriteAllLines(path, tail);
                    count = tail.Length;
                }

                Counts[key] = count;
            }
        }
        catch { }
    }

    /// <summary>Blocks for replay, oldest first, capped to the newest <see cref="MaxBlocks"/>
    /// (the file is rewritten to the cap when it exceeds it).</summary>
    public static IReadOnlyList<string> Load(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return Array.Empty<string>();

            List<string> lines;
            lock (Gate) lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();

            if (lines.Count > MaxBlocks)
            {
                lines = lines.Skip(lines.Count - MaxBlocks).ToList();
                lock (Gate) File.WriteAllLines(path, lines);
            }
            lock (Gate) Counts[key] = lines.Count;

            var blocks = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                try
                {
                    var html = JsonSerializer.Deserialize<string>(line);
                    if (!string.IsNullOrEmpty(html)) blocks.Add(html);
                }
                catch { /* one corrupt line (torn write at crash) — skip it, keep the rest */ }
            }
            return blocks;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Forgets a session's journal — used by /clear and when a tab is closed.</summary>
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

    /// <summary>Removes journals that no longer belong to any open tab (closed while the
    /// app couldn't clean up, e.g. after a crash).</summary>
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
