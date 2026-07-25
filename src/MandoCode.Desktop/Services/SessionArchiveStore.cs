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

    /// <summary>First thing the user said, trimmed — the line that says what this conversation was
    /// ABOUT. Paired with <see cref="LastMessage"/>, which says where it stopped.</summary>
    public string? Preview { get; init; }

    /// <summary>
    /// Last thing the user said, trimmed — "where you left off", the line that answers *should I
    /// resume this*. The user's turn rather than the agent's: it's symmetric with
    /// <see cref="Preview"/>, and it's your own instruction rather than a long formatted reply.
    ///
    /// Empty string means "computed, nothing worth showing" — a single-turn conversation (where it
    /// would just repeat <see cref="Preview"/>) or one with no user turns. That's deliberately
    /// DISTINCT from null, which means "archived before this field existed and still needs
    /// backfilling"; settable for exactly that backfill.
    /// </summary>
    public string? LastMessage { get; set; }

    // ---- display helpers for the panel ----

    [System.Text.Json.Serialization.JsonIgnore]
    public string TimeLabel => ProjectDisplay.TimeLabel(ClosedAt);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectLabel => ProjectDisplay.ProjectLabel(ProjectRoot);

    /// <summary>Card body: the first user message, or an honest stand-in when there wasn't one.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PreviewOrPlaceholder =>
        string.IsNullOrWhiteSpace(Preview) ? "(no message text captured)" : Preview!;

    /// <summary>
    /// Why this row matched a full-text search: a window around the hit INSIDE the conversation.
    /// Set by the History panel on every populate (null when the search matched on metadata alone,
    /// or when there's no search) and never persisted. A row can match on text the title and preview
    /// don't contain, so without this the hit would look arbitrary. Mutable and display-only — the
    /// rest of this type is immutable index data.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? MatchSnippet { get; set; }
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

    /// <summary>
    /// One-time migration for rows archived before <see cref="SessionArchiveEntry.LastMessage"/>
    /// existed, so old and new cards look the same instead of only new ones carrying a last line.
    /// <paramref name="resolve"/> does the per-session file read and MUST return "" (not null) when
    /// there's nothing to show, otherwise the row is retried on every launch. Persists once for the
    /// whole batch. Safe to call from a background thread — <see cref="Changed"/> is documented as
    /// possibly arriving off the UI thread.
    /// </summary>
    public int BackfillLastMessages(Func<SessionArchiveEntry, string?> resolve)
    {
        List<SessionArchiveEntry> pending;
        lock (_lock) pending = _items.Where(e => e.LastMessage == null).ToList();
        if (pending.Count == 0) return 0;

        var filled = 0;
        foreach (var entry in pending)
        {
            var value = resolve(entry);
            if (value == null) continue;   // resolve failed outright — leave it for next time
            entry.LastMessage = value;
            filled++;
        }
        if (filled == 0) return 0;

        Persist();
        Changed?.Invoke();
        return filled;
    }

    /// <summary>
    /// Removes a batch of rows in ONE pass — a single <see cref="Persist"/> and a single
    /// <see cref="Changed"/> for the whole set. Looping <see cref="Remove"/> would rewrite the index
    /// file and rebuild the History panel once per row, which is what makes clearing a whole project
    /// group visibly slow. Keys not in the index are ignored, so a caller working from a stale group
    /// snapshot is safe. Returns how many rows actually went.
    /// </summary>
    public int RemoveAll(IEnumerable<string> keys, bool deleteFiles)
    {
        var targets = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return 0;

        int removed;
        lock (_lock) removed = _items.RemoveAll(e => targets.Contains(e.Key));
        if (removed == 0) return 0;

        // Files are deleted outside the lock — same order as Remove, and file IO shouldn't block
        // another thread filing a closed session.
        if (deleteFiles) foreach (var key in targets) DeleteFiles(key);

        Persist();
        Changed?.Invoke();
        return removed;
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
