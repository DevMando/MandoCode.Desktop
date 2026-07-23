using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>
/// One closed conversation, recoverable from the History panel. Pure data — the heavy parts
/// (transcript HTML, model memory) stay in their own per-key stores; this is just the index row
/// that lets the user find and reopen them.
/// </summary>
public sealed class SessionArchiveEntry
{
    /// <summary>The session's durable persist-key — the join back to its transcript journal,
    /// conversation log, and history JSON on disk. Reopening recreates a tab on this key so the
    /// existing restore cascade rehydrates it.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }
    public required string ProjectRoot { get; init; }

    /// <summary>Model the conversation last ran on (null if never set), re-selected on reopen.</summary>
    public string? Model { get; init; }

    public required DateTimeOffset ClosedAt { get; init; }

    /// <summary>User+assistant turns recorded for this session — a cheap "how big was this".</summary>
    public required int TurnCount { get; init; }

    /// <summary>First thing the user said, trimmed — the line that makes a row recognizable.</summary>
    public string? Preview { get; init; }

    // ---- display helpers for the panel ----

    [System.Text.Json.Serialization.JsonIgnore]
    public string TimeLabel => ClosedAt.LocalDateTime.ToString("MMM d · h:mm tt");

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectRoot)) return "Unknown project";
            var name = Path.GetFileName(
                ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? ProjectRoot : name;
        }
    }

    /// <summary>Card body: the first user message, or an honest stand-in when there wasn't one.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PreviewOrPlaceholder =>
        string.IsNullOrWhiteSpace(Preview) ? "(no message text captured)" : Preview!;
}

/// <summary>
/// App-wide index of CLOSED conversations, so a tab you closed can be reopened later rather than
/// being gone for good. This is the retention half of a deliberate split:
///
///   • Closing a tab ARCHIVES it — the journals stay on disk and a row lands here.
///   • <c>/clear</c> still FORGETS — it deletes the journals and never archives (see AgentSession).
///
/// "Cleared means cleared" survives; only the meaning of *closing* softens from "gone" to
/// "recoverable". Persisted to <c>sessions.json</c> and rewritten on every change, exactly like
/// <see cref="SnapshotStore"/>. A retention cap bounds the archive: evicting a row also deletes its
/// journal/log/history files, so the on-disk stores can't grow without limit.
///
/// <see cref="Changed"/> may fire on a background thread; subscribers marshal to the UI themselves.
/// </summary>
public sealed class SessionArchiveStore
{
    /// <summary>Newest N closed sessions are kept; older rows are evicted with their files.</summary>
    private const int MaxEntries = 60;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "sessions.json");

    private readonly object _lock = new();
    private readonly List<SessionArchiveEntry> _items = new();

    public SessionArchiveStore()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var loaded = JsonSerializer.Deserialize<List<SessionArchiveEntry>>(File.ReadAllText(StorePath));
            if (loaded != null) _items.AddRange(loaded);
        }
        catch { /* corrupt/unreadable index — start empty rather than crash the app */ }
    }

    /// <summary>Raised after any add/remove. May arrive on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A point-in-time copy, newest first.</summary>
    public IReadOnlyList<SessionArchiveEntry> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    /// <summary>Persist-keys of every archived session — folded into the startup orphan sweep's
    /// keep-set so an archived conversation's files aren't mistaken for a crash leftover.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_lock) return _items.Select(e => e.Key).ToList(); }
    }

    public bool Contains(string key)
    {
        lock (_lock) return _items.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Files a closed session. Newest-first; a re-closed session (reopened from the archive, then
    /// closed again) replaces its old row rather than duplicating it. Evicts past the cap, deleting
    /// the evicted sessions' on-disk files so nothing is orphaned.
    /// </summary>
    public void Add(SessionArchiveEntry entry)
    {
        List<SessionArchiveEntry> evicted = new();
        lock (_lock)
        {
            _items.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, entry);
            while (_items.Count > MaxEntries)
            {
                evicted.Add(_items[^1]);
                _items.RemoveAt(_items.Count - 1);
            }
        }
        foreach (var e in evicted) DeleteFiles(e.Key);
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Removes a row and deletes its files — the History panel's Delete, and the path a
    /// reopened session takes out of the archive (its files stay; only the index row goes).</summary>
    public void Remove(string key, bool deleteFiles)
    {
        bool removed;
        lock (_lock)
            removed = _items.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) return;
        if (deleteFiles) DeleteFiles(key);
        Persist();
        Changed?.Invoke();
    }

    private static void DeleteFiles(string key)
    {
        TranscriptJournal.Delete(key);
        ConversationLog.Delete(key);
        SessionHistoryStore.Delete(key);
    }

    private void Persist()
    {
        try
        {
            List<SessionArchiveEntry> copy;
            lock (_lock) copy = _items.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(copy));
        }
        catch { /* persistence is best-effort; the in-memory index is still correct */ }
    }
}
