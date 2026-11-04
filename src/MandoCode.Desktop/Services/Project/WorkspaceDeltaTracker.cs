namespace MandoCode.Desktop.Services;

/// <summary>
/// Decides what to tell the model about workspace changes made OUTSIDE the conversation
/// (external edits, terminal commits, branch switches, the undo button's checkouts).
/// Pure logic, no UI: ChatTabView feeds it git snapshots and watcher touches; it returns
/// note strings for the next message's preamble. Thread-safe — watcher threads record
/// touches while the UI thread captures and emits.
///
/// Lifecycle: <see cref="MarkCapturePending"/> when a turn ends (or an undo rewrites files)
/// → <see cref="CaptureBaselineIfPending"/> when the next git snapshot lands →
/// <see cref="EmitDelta"/> at send time. While a capture is pending the stored baseline is
/// STALE (it predates the agent's own edits), so EmitDelta stays silent rather than
/// misattribute the agent's work to the outside world — silence over lies; anything real
/// is still reported one turn later once a fresh baseline lands.
/// </summary>
public sealed class WorkspaceDeltaTracker
{
    private readonly object _lock = new();
    private Dictionary<string, string>? _baseline;   // relPath → kind at last turn end
    private string? _baselineBranch;
    private string? _baselineOid;
    private bool _capturePending;
    private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The current baseline no longer reflects reality (a turn just ended, or an
    /// undo rewrote files) — recapture on the next snapshot; emit nothing until then.</summary>
    public void MarkCapturePending()
    {
        lock (_lock) _capturePending = true;
    }

    public void CaptureBaselineIfPending(GitBranchInfo? info)
    {
        lock (_lock)
        {
            if (!_capturePending) return;
            CaptureLocked(info);
        }
    }

    /// <summary>A file was touched while the agent was idle. Content edits to files that are
    /// ALREADY dirty don't move their git-status entry, so the snapshot diff alone can't see
    /// them — this set fills that gap.</summary>
    public void RecordTouch(string relPath)
    {
        lock (_lock) _touched.Add(relPath);
    }

    /// <summary>Diffs the current snapshot against the baseline and re-baselines. Call at
    /// send time; deliver the returned notes with the outgoing message.</summary>
    public IReadOnlyList<string> EmitDelta(GitBranchInfo? current)
    {
        lock (_lock)
        {
            // Pending capture = the baseline predates the agent's last edits. Diffing now
            // would report the agent's own work as external. Stay silent this turn.
            if (_capturePending) return Array.Empty<string>();

            if (_baseline == null || current == null)
            {
                CaptureLocked(current);   // first send / non-git folder — nothing to compare
                return Array.Empty<string>();
            }

            var notes = new List<string>();
            var branchChanged = _baselineBranch != null && current.Branch != _baselineBranch;
            if (branchChanged)
                notes.Add($"The git branch changed from '{_baselineBranch}' to '{current.Branch}'.");

            var cur = current.Changes.ToDictionary(c => c.RelPath, c => c.Kind, StringComparer.Ordinal);
            var appeared = cur.Keys.Where(k => !_baseline.ContainsKey(k));
            var resolved = _baseline.Keys.Where(k => !cur.ContainsKey(k))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            var editedInPlace = _touched.Where(t => cur.ContainsKey(t) && _baseline.ContainsKey(t));

            var changedOnDisk = appeared.Concat(editedInPlace).Distinct()
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (changedOnDisk.Count > 0)
                notes.Add("Files changed on disk: " + JoinCapped(changedOnDisk));

            if (resolved.Count > 0)
            {
                // HEAD movement disambiguates commit vs revert — but only when the branch
                // didn't also change (a checkout moves HEAD without committing anything).
                var headMoved = !string.IsNullOrEmpty(_baselineOid)
                    && current.Oid.Length > 0 && current.Oid != _baselineOid;
                var phrasing = branchChanged
                    ? "Files that no longer have uncommitted changes after the branch change: "
                    : headMoved
                        ? "Files COMMITTED outside this conversation (a new commit exists): "
                        : "Files whose uncommitted changes were REVERTED/discarded outside this conversation: ";
                notes.Add(phrasing + JoinCapped(resolved));
            }

            CaptureLocked(current);
            return notes;
        }
    }

    private void CaptureLocked(GitBranchInfo? info)
    {
        _baseline = info?.Changes.ToDictionary(c => c.RelPath, c => c.Kind, StringComparer.Ordinal);
        _baselineBranch = info?.Branch;
        _baselineOid = info?.Oid;
        _touched.Clear();
        _capturePending = false;
    }

    private static string JoinCapped(List<string> paths) => paths.Count <= 10
        ? string.Join(", ", paths)
        : string.Join(", ", paths.Take(10)) + $" (+{paths.Count - 10} more)";
}
