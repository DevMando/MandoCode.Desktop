namespace MandoCode.Desktop.Services;

/// <summary>
/// App-wide store of <see cref="ContextSnapshot"/>s — one list shared by every tab, so a snapshot
/// captured while Agent 1 switches models can be imported into a brand-new tab running a capable
/// model. Session-scoped: it lives for the life of the process and is not persisted (matching the
/// app's "settings reset on launch" stance; disk persistence is a possible follow-up).
///
/// <see cref="Changed"/> can fire on a background thread (captures happen during a model switch),
/// so subscribers must marshal to the UI thread themselves.
/// </summary>
public sealed class SnapshotStore
{
    private readonly object _lock = new();
    private readonly List<ContextSnapshot> _items = new();
    private int _nextId;

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
        string? name = null)
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
            };
            _items.Insert(0, snapshot);   // newest first
        }
        Changed?.Invoke();
        return snapshot;
    }

    public void Remove(ContextSnapshot snapshot)
    {
        bool removed;
        lock (_lock) removed = _items.Remove(snapshot);
        if (removed) Changed?.Invoke();
    }
}
