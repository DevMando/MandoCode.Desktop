namespace MandoCode.Desktop.Services;

/// <summary>
/// Lazily-loaded cache of archived conversations' searchable text, so History's full-text search
/// reads each log from disk once instead of once per keystroke (the archive caps at 60 sessions, so
/// an un-cached search would be up to 60 file reads per character typed).
///
/// The search runs on a background thread, so every member is lock-guarded. Entries revalidate
/// against the log file's last-write time rather than living forever: a session reopened and closed
/// again gets re-read instead of matched against stale text.
///
/// Bounded in practice by the archive cap; <see cref="Forget"/> drops rows the user deleted so the
/// dictionary doesn't accumulate keys whose files are gone.
/// </summary>
public sealed class ConversationTextCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime? Stamp, string Text)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The conversation's searchable text, loading it on first use. Safe from any thread.</summary>
    public string TextFor(string key)
    {
        var stamp = ConversationLog.LastWriteUtc(key);

        lock (_lock)
            if (_cache.TryGetValue(key, out var hit) && hit.Stamp == stamp)
                return hit.Text;

        // Read and parse OUTSIDE the lock — one slow log shouldn't serialise every other key's
        // lookup. A duplicate concurrent load is harmless: both produce the same text.
        var text = ConversationSearch.Flatten(ConversationLog.Load(key));

        lock (_lock) _cache[key] = (stamp, text);
        return text;
    }

    /// <summary>Drops cached text for keys that are going away (a deleted row or group).</summary>
    public void Forget(IEnumerable<string> keys)
    {
        lock (_lock)
            foreach (var key in keys) _cache.Remove(key);
    }
}
