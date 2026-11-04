using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>
/// App-wide store of <see cref="ContextSnapshot"/>s — one list shared by every tab, so a snapshot
/// captured while Agent 1 switches models can be imported into a brand-new tab running a capable
/// model. PERSISTED: loaded from disk at construction and rewritten on every add/remove
/// (best-effort — a failed write never breaks the in-memory store), so snapshots survive
/// app restarts.
///
/// <see cref="Changed"/> can fire on a background thread (captures happen during a model switch),
/// so subscribers must marshal to the UI thread themselves.
/// </summary>
public sealed class SnapshotStore
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "snapshots.json");

    private readonly object _lock = new();
    private readonly List<ContextSnapshot> _items = new();
    private int _nextId;

    public SnapshotStore()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var loaded = JsonSerializer.Deserialize<List<ContextSnapshot>>(File.ReadAllText(StorePath));
            if (loaded == null) return;
            _items.AddRange(loaded);
            _nextId = _items.Count == 0 ? 0 : _items.Max(s => s.Id);
        }
        catch { /* corrupt/unreadable store — start empty rather than crash the app */ }
    }

    private void Persist()
    {
        try
        {
            List<ContextSnapshot> copy;
            lock (_lock) copy = _items.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(copy));
        }
        catch { /* persistence is best-effort; the in-memory store is still correct */ }
    }

    /// <summary>Raised after any add/remove. May arrive on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A point-in-time copy, newest first. Safe to bind to a flyout.</summary>
    public IReadOnlyList<ContextSnapshot> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public ContextSnapshot Add(string originModel, string summarizerModel, string recap, int messageCount,
        string? name = null, string? projectRoot = null)
    {
        ContextSnapshot snapshot;
        lock (_lock)
        {
            snapshot = new ContextSnapshot
            {
                Id = ++_nextId,
                CapturedAt = DateTimeOffset.Now,
                OriginModel = originModel,
                SummarizerModel = summarizerModel,
                Recap = recap,
                MessageCount = messageCount,
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                ProjectRoot = projectRoot,
            };
            _items.Insert(0, snapshot);   // newest first
        }
        Persist();
        Changed?.Invoke();
        return snapshot;
    }

    public void Remove(ContextSnapshot snapshot)
    {
        bool removed;
        lock (_lock) removed = _items.Remove(snapshot);
        if (!removed) return;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>
    /// Removes a batch in ONE pass — a single <see cref="Persist"/> and a single
    /// <see cref="Changed"/> for the whole set. Looping <see cref="Remove"/> would rewrite the
    /// store file and rebuild the panel once per snapshot, which is what makes deleting a whole
    /// project group visibly slow. Matched by <see cref="ContextSnapshot.Id"/> (unique per
    /// snapshot) rather than reference, so a caller holding a deserialized copy still works.
    /// Returns how many were actually present.
    /// </summary>
    public int RemoveAll(IEnumerable<ContextSnapshot> snapshots)
    {
        var ids = snapshots.Select(s => s.Id).ToHashSet();
        if (ids.Count == 0) return 0;

        int removed;
        lock (_lock) removed = _items.RemoveAll(s => ids.Contains(s.Id));
        if (removed == 0) return 0;

        Persist();
        Changed?.Invoke();
        return removed;
    }
}
