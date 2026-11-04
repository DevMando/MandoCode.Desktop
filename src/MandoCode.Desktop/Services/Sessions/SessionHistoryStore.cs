namespace MandoCode.Desktop.Services;

/// <summary>
/// Full-fidelity model memory per session: the JSON that AIService.ExportHistoryJson
/// produces, written whole-file at each turn end and rehydrated on session restore via
/// TryRestoreHistoryJson. This is the highest tier of session persistence — when it
/// applies, the restored agent genuinely REMEMBERS the conversation (tool calls included)
/// instead of being briefed about it; ConversationLog's tail-brief is the fallback.
/// Best-effort everywhere, like its siblings.
/// </summary>
public static class SessionHistoryStore
{
    /// <summary>Safety valve: a history JSON beyond this size isn't persisted (the harness's
    /// own compaction shrinks the live history long before this in practice).</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly object Gate = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "histories");

    private static string PathFor(string key) => Path.Combine(Folder, key + ".json");

    public static void Save(string key, string? json)
    {
        try
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxBytes) return;
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                // Write-then-rename so a crash mid-write can't leave a torn file where a
                // good previous export used to be.
                var tmp = PathFor(key) + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, PathFor(key), overwrite: true);
            }
        }
        catch { }
    }

    public static string? Load(string key)
    {
        try
        {
            var path = PathFor(key);
            lock (Gate) return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string key)
    {
        try { lock (Gate) File.Delete(PathFor(key)); } catch { }
    }

    public static void Sweep(IEnumerable<string> liveKeys)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var keep = new HashSet<string>(liveKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }
}
