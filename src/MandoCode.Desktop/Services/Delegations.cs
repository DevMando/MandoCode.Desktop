namespace MandoCode.Desktop.Services;

public enum DelegationState { Running, Done, Failed }

/// <summary>One job one agent handed to another.</summary>
public sealed record Delegation(
    string Id,
    string FromKey,
    string FromName,
    string ToKey,
    string ToName,
    string Task,
    DateTimeOffset StartedAt,
    DelegationState State = DelegationState.Running,
    string? Result = null,
    DateTimeOffset? FinishedAt = null);

/// <summary>
/// Outstanding delegations, and the rolling digest each one contributes to its owner's inbox.
///
/// <para>The digest is ASSEMBLED, never written by a model: every field comes from state the host
/// already keeps — the turn gate, plan progress, the command log, the working-tree diff. So keeping
/// it current costs string formatting rather than a turn, it is always accurate, and it cannot
/// invent progress. A model-written précis would cost a call per update and could report a job as
/// nearly finished because it read as though it were.</para>
/// </summary>
public sealed class DelegationRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Delegation> _byId = new(StringComparer.Ordinal);
    private int _next;

    public Delegation Open(string fromKey, string fromName, string toKey, string toName, string task)
    {
        lock (_lock)
        {
            var d = new Delegation($"d{++_next}", fromKey, fromName, toKey, toName, task, DateTimeOffset.Now);
            _byId[d.Id] = d;
            return d;
        }
    }

    public Delegation? Get(string id)
    {
        lock (_lock) { return _byId.TryGetValue(id, out var d) ? d : null; }
    }

    /// <summary>Delegations this agent is waiting on, newest first.</summary>
    public IReadOnlyList<Delegation> OpenedBy(string fromKey)
    {
        lock (_lock)
        {
            return _byId.Values.Where(d => d.FromKey == fromKey)
                        .OrderByDescending(d => d.StartedAt).ToList();
        }
    }

    public Delegation? Complete(string id, DelegationState state, string? result)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var d)) return null;
            var done = d with { State = state, Result = result, FinishedAt = DateTimeOffset.Now };
            _byId[id] = done;
            return done;
        }
    }

    /// <summary>Forgets finished delegations older than <paramref name="keepFor"/>, so the registry
    /// does not accumulate a session's worth of completed work.</summary>
    public void Sweep(TimeSpan keepFor)
    {
        var cutoff = DateTimeOffset.Now - keepFor;
        lock (_lock)
        {
            foreach (var id in _byId.Where(kv => kv.Value.FinishedAt is { } f && f < cutoff)
                                    .Select(kv => kv.Key).ToList())
                _byId.Remove(id);
        }
    }

    // ---- Digest -----------------------------------------------------------------

    /// <summary>The inbox id a delegation's progress is always filed under, so each new report
    /// REPLACES the last rather than stacking up.</summary>
    public static string InboxId(Delegation d) => $"delegation:{d.Id}";

    /// <summary>
    /// Where a delegated job stands, assembled from live state. <paramref name="peer"/> is the
    /// target's directory entry, or null when its tab has closed.
    /// </summary>
    public static InboxMessage Digest(Delegation d, AgentEntry? peer, IReadOnlyList<string> recentCommands)
    {
        var age = Age(DateTimeOffset.Now - d.StartedAt);
        var lines = new List<string> { $"You asked {d.ToName} to: {d.Task}" };

        switch (d.State)
        {
            case DelegationState.Done:
                lines.Add($"FINISHED after {Age(d.FinishedAt - d.StartedAt ?? TimeSpan.Zero)}.");
                if (!string.IsNullOrWhiteSpace(d.Result)) lines.Add($"{d.ToName} reported: {d.Result}");
                break;

            case DelegationState.Failed:
                lines.Add($"DID NOT FINISH: {d.Result ?? "no reason given"}.");
                break;

            default:
                if (peer == null)
                {
                    lines.Add($"STOPPED — {d.ToName}'s tab was closed before it finished.");
                    break;
                }
                lines.Add($"Still working ({age} so far).");
                if (peer.PlanTotal > 0) lines.Add($"On step {peer.PlanStep} of {peer.PlanTotal}.");
                if (peer.IsRunningCommand) lines.Add("A command is running right now.");
                if (recentCommands.Count > 0)
                    lines.Add("Recent commands: " + string.Join(" · ", recentCommands.TakeLast(4)));
                break;
        }

        return new InboxMessage(
            InboxId(d),
            $"{d.ToName} — {Headline(d)}",
            string.Join("\n", lines),
            DateTimeOffset.Now);
    }

    /// <summary>
    /// The one-line card announcing a finished job, shown in the DELEGATING agent's transcript.
    ///
    /// <para>Leads with the RESULT, not the brief. The first version echoed the whole task back —
    /// a paragraph of instructions the user had just watched their agent compose — and never said
    /// what actually happened. The brief survives only as a short label, enough to tell two jobs
    /// apart when several are outstanding.</para>
    /// </summary>
    public static string CompletionLine(Delegation d)
    {
        var label = Shorten(d.Task, 70);
        if (d.State != DelegationState.Done)
            return $"✗ {d.ToName} did not finish \"{label}\" — {Shorten(d.Result ?? "no reason given", 160)}";

        var outcome = Shorten(FirstMeaningfulLine(d.Result), 200);
        return string.IsNullOrEmpty(outcome)
            ? $"✓ {d.ToName} finished \"{label}\""
            : $"✓ {d.ToName} finished \"{label}\" — {outcome}";
    }

    /// <summary>
    /// The first line of a reply that carries any content. Models routinely open with a blank line
    /// or a bare "Done —", and a card that showed only that would say nothing at all.
    /// </summary>
    private static string FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '*', '-', ' ');
            if (line.Length > 12) return line;
        }
        return text.Trim();
    }

    /// <summary>Cuts at a word boundary where one is close, so a label does not end mid-word.</summary>
    private static string Shorten(string text, int max)
    {
        text = text.Trim().Replace('\n', ' ');
        if (text.Length <= max) return text;
        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ' ') + "…";
    }

    private static string Headline(Delegation d) => d.State switch
    {
        DelegationState.Done => "finished the job you delegated",
        DelegationState.Failed => "could not finish the job you delegated",
        _ => "working on the job you delegated",
    };

    /// <summary>Coarse on purpose — "6m" is what the reader needs, and a precise figure in a digest
    /// that is rebuilt constantly only invites the model to quote a number that has already moved.</summary>
    private static string Age(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)}s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m"
        : $"{(int)t.TotalHours}h{t.Minutes:D2}m";
}
