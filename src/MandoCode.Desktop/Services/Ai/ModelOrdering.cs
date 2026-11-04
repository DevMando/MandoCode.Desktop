namespace MandoCode.Desktop.Services;

/// <summary>
/// App-wide ordering for the Settings model picker: pinned models first, then the ones this user
/// actually reaches for, then everything else alphabetically. Persisted in panel-state.json beside
/// the other window-level preferences rather than in the per-agent config — "these are my models"
/// describes the person, not one agent, the same way <see cref="AgentCallsigns"/> does.
/// </summary>
public static class ModelOrdering
{
    /// <summary>Bounded on purpose: a long tail stops being "recent" and just becomes the list again.</summary>
    public const int MaxRecent = 5;

    private static readonly List<string> PinnedModels = [];
    private static readonly List<string> RecentModels = [];

    /// <summary>Raised when a pin or a use changes the order, so the host can persist it.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<string> Pinned => PinnedModels;
    public static IReadOnlyList<string> Recent => RecentModels;

    public static void Load(IEnumerable<string>? pinned, IEnumerable<string>? recent)
    {
        PinnedModels.Clear();
        RecentModels.Clear();
        if (pinned != null) PinnedModels.AddRange(pinned.Where(m => !string.IsNullOrWhiteSpace(m)));
        if (recent != null) RecentModels.AddRange(recent.Where(m => !string.IsNullOrWhiteSpace(m)).Take(MaxRecent));
    }

    public static bool IsPinned(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        PinnedModels.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));

    public static void TogglePin(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        var existing = PinnedModels.FindIndex(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) PinnedModels.RemoveAt(existing);
        else PinnedModels.Add(model);
        Changed?.Invoke();
    }

    /// <summary>
    /// Records that a model was actually selected. Recency of USE beats recency of pull: a model
    /// tried once and abandoned would otherwise outrank the one running all day, and it works the
    /// same for cloud models, which are never pulled locally and so carry no useful pull date.
    /// </summary>
    public static void NoteUsed(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        if (RecentModels.FirstOrDefault() is { } head &&
            string.Equals(head, model, StringComparison.OrdinalIgnoreCase)) return;   // already on top
        RecentModels.RemoveAll(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
        RecentModels.Insert(0, model);
        while (RecentModels.Count > MaxRecent) RecentModels.RemoveAt(RecentModels.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>
    /// Pinned first, then recently used, then the rest alphabetically. The tail stays alphabetical
    /// deliberately: an order that reshuffles whenever a model is pulled destroys the muscle memory
    /// that makes a long dropdown usable at all. Only the two short, meaningful groups move.
    /// </summary>
    public static List<string> Arrange(IEnumerable<string> models)
    {
        var remaining = new List<string>(models);
        var ordered = new List<string>();

        void Take(IEnumerable<string> wanted)
        {
            foreach (var want in wanted)
            {
                var i = remaining.FindIndex(m => string.Equals(m, want, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;   // pinned or recently used, but no longer installed
                ordered.Add(remaining[i]);
                remaining.RemoveAt(i);
            }
        }

        Take(PinnedModels.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        Take(RecentModels);
        ordered.AddRange(remaining.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }
}
