namespace MandoCode.Desktop.Services;

public enum DelegationState { Running, Done, Failed }

/// <summary>
/// Why one agent handed work to another. Both travel the identical path — the target takes a real
/// turn either way — so this changes only how the result is WORDED back to the asker. A question
/// answered is not a job finished, and a card reading "finished" for a question asked in passing
/// would suggest work had been done.
/// </summary>
public enum DelegationKind { Job, Question }

/// <summary>One piece of work one agent handed to another.</summary>
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
    DateTimeOffset? FinishedAt = null,
    DelegationKind Kind = DelegationKind.Job);

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

    public Delegation Open(string fromKey, string fromName, string toKey, string toName, string task,
                           DelegationKind kind = DelegationKind.Job)
    {
        lock (_lock)
        {
            var d = new Delegation($"d{++_next}", fromKey, fromName, toKey, toName, task,
                                   DateTimeOffset.Now, Kind: kind);
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
        var ask = d.Kind == DelegationKind.Question;
        var lines = new List<string>
        {
            ask ? $"You asked {d.ToName}: {d.Task}" : $"You asked {d.ToName} to: {d.Task}"
        };

        switch (d.State)
        {
            case DelegationState.Done:
                lines.Add(ask
                    ? $"ANSWERED after {Age(d.FinishedAt - d.StartedAt ?? TimeSpan.Zero)}."
                    : $"FINISHED after {Age(d.FinishedAt - d.StartedAt ?? TimeSpan.Zero)}.");
                // The reply is carried whole. This is the copy the model reads on its next turn, so
                // trimming it here would make the clipped version the only one it ever sees.
                if (!string.IsNullOrWhiteSpace(d.Result))
                    lines.Add(ask ? $"{d.ToName} replied: {d.Result}" : $"{d.ToName} reported: {d.Result}");
                break;

            case DelegationState.Failed:
                lines.Add(ask
                    ? $"DID NOT ANSWER: {d.Result ?? "no reason given"}."
                    : $"DID NOT FINISH: {d.Result ?? "no reason given"}.");
                break;

            default:
                if (peer == null)
                {
                    lines.Add($"STOPPED — {d.ToName}'s tab was closed before it finished.");
                    break;
                }
                lines.Add(ask
                    ? $"Still working out an answer ({age} so far)."
                    : $"Still working ({age} so far).");
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
        var ask = d.Kind == DelegationKind.Question;
        var label = Shorten(d.Task, 70);

        if (d.State != DelegationState.Done)
            return ask
                ? $"✗ {d.ToName} could not answer \"{label}\" — {Shorten(d.Result ?? "no reason given", 160)}"
                : $"✗ {d.ToName} did not finish \"{label}\" — {Shorten(d.Result ?? "no reason given", 160)}";

        // An answer gets far more room than a job's outcome. For a job the interesting thing is
        // THAT it finished — the work itself is in the files. For a question the reply IS the
        // deliverable, and clipping it to a job's length would send the user to the other agent's
        // tab to read two sentences.
        var outcome = ask
            ? Shorten(d.Result?.Trim() ?? "", 600)
            : Shorten(FirstMeaningfulLine(d.Result), 200);

        if (string.IsNullOrEmpty(outcome))
            return ask ? $"↩ {d.ToName} replied to \"{label}\"" : $"✓ {d.ToName} finished \"{label}\"";

        return ask
            ? $"↩ {d.ToName} replied to \"{label}\" — {outcome}"
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

    private static string Headline(Delegation d) => (d.Kind, d.State) switch
    {
        (DelegationKind.Question, DelegationState.Done) => "answered your question",
        (DelegationKind.Question, DelegationState.Failed) => "could not answer your question",
        (DelegationKind.Question, _) => "working out an answer for you",
        (_, DelegationState.Done) => "finished the job you delegated",
        (_, DelegationState.Failed) => "could not finish the job you delegated",
        _ => "working on the job you delegated",
    };

    /// <summary>Coarse on purpose — "6m" is what the reader needs, and a precise figure in a digest
    /// that is rebuilt constantly only invites the model to quote a number that has already moved.</summary>
    private static string Age(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)}s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m"
        : $"{(int)t.TotalHours}h{t.Minutes:D2}m";
}
