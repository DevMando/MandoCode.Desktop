namespace MandoCode.Desktop.Services;

/// <summary>One open agent, as everything outside its own tab sees it.</summary>
/// <param name="Key">Stable identity — the session's PersistKey, which survives renames and restarts.</param>
/// <param name="Name">The callsign the user types after '@'.</param>
public sealed record AgentEntry(
    string Key,
    string Name,
    string ProjectRoot,
    string FolderLabel,
    string Model,
    bool IsBusy,
    bool IsRunningCommand,
    int PlanStep,
    int PlanTotal);

/// <summary>
/// The host's register of open agents, so one agent can be addressed by another. Populated by
/// MainWindow, which owns the tab list; read by the '@' picker, by the mention expander, and by the
/// cross-agent tools.
///
/// <para>Why a service and not just a walk over MainWindow's tabs: the three readers sit in
/// different layers (a control, a view-model, and a host tool), and none of them should hold a
/// reference to the window. This is the one place that knows what agents exist.</para>
///
/// <para><b>Snapshot semantics.</b> <see cref="All"/> returns a copy taken under the lock. An agent
/// can be closed a moment after a caller reads it, so callers resolve by <see cref="AgentEntry.Key"/>
/// at the point of use and treat "gone" as an ordinary outcome rather than an error.</para>
///
/// <para><b>Threading:</b> refreshed from the UI thread, read from anywhere — tool calls arrive on
/// model-loop threads.</para>
/// </summary>
public sealed class AgentDirectory
{
    private readonly object _lock = new();
    private List<AgentEntry> _agents = new();
    private readonly Dictionary<string, IAgentPeer> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentInbox> _inboxes = new(StringComparer.Ordinal);

    /// <summary>Jobs agents have handed to each other, shared by every tab.</summary>
    public DelegationRegistry Delegations { get; } = new();

    /// <summary>An agent's mailbox, created on first use so a peer can post to an agent that has
    /// not yet had reason to look at its own.</summary>
    public AgentInbox InboxFor(string key)
    {
        lock (_lock)
        {
            if (!_inboxes.TryGetValue(key, out var box)) _inboxes[key] = box = new AgentInbox();
            return box;
        }
    }

    /// <summary>Raised after the register changes, so the UI can re-offer suggestions.</summary>
    public event Action? Changed;

    public IReadOnlyList<AgentEntry> All
    {
        get { lock (_lock) { return _agents.ToList(); } }
    }

    /// <summary>Replaces the register wholesale. Cheaper to rebuild than to diff — the list is
    /// never more than a handful of entries, and a rebuild cannot drift out of sync.</summary>
    public void Replace(IEnumerable<AgentEntry> agents)
    {
        lock (_lock) { _agents = agents.ToList(); }
        Changed?.Invoke();
    }

    /// <summary>
    /// Registers the live agent behind a key, so another agent can actually address it. Separate
    /// from <see cref="Replace"/> because the entries are a display snapshot that is republished
    /// constantly, whereas a peer lives as long as its tab.
    /// </summary>
    public void RegisterPeer(IAgentPeer peer)
    {
        lock (_lock) { _peers[peer.Key] = peer; }
    }

    public void RemovePeer(string key)
    {
        lock (_lock) { _peers.Remove(key); }
    }

    /// <summary>The live agent for a key, or null if its tab has since closed.</summary>
    public IAgentPeer? Peer(string key)
    {
        lock (_lock) { return _peers.TryGetValue(key, out var p) ? p : null; }
    }

    /// <summary>
    /// Resolves a typed mention. Case-insensitive, and exact matches win outright — a partial match
    /// is only offered when nothing matches exactly, so an agent named "Ninja" is never shadowed by
    /// one named "NinjaTwo".
    /// </summary>
    public AgentEntry? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var agents = All;
        return agents.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? SingleOrNoneStartingWith(agents, name);
    }

    /// <summary>A prefix match, but only when exactly one agent matches — an ambiguous prefix
    /// resolves to nothing rather than guessing which agent the user meant.</summary>
    private static AgentEntry? SingleOrNoneStartingWith(IReadOnlyList<AgentEntry> agents, string prefix)
    {
        AgentEntry? found = null;
        foreach (var a in agents)
        {
            if (!a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null) return null;
            found = a;
        }
        return found;
    }

    /// <summary>
    /// Agents whose callsign matches what has been typed so far, for the '@' picker. Ordered so
    /// that a prefix match beats a mid-word one — typing "ni" should offer Ninja before Hornight.
    /// <paramref name="excludeKey"/> drops the asking agent, which cannot mention itself.
    /// </summary>
    public IReadOnlyList<AgentEntry> Match(string fragment, string? excludeKey)
    {
        var agents = All.Where(a => a.Key != excludeKey);
        if (string.IsNullOrEmpty(fragment)) return agents.OrderBy(a => a.Name).ToList();

        return agents
            .Where(a => a.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Name)
            .ToList();
    }

    /// <summary>Recent shell commands run by an agent, for its delegation digest. Empty when the
    /// host has not supplied a source — the digest simply omits that line.</summary>
    public Func<string, IReadOnlyList<string>>? CommandsProvider { get; set; }

    public IReadOnlyList<string> RecentCommandsFor(string key) =>
        CommandsProvider?.Invoke(key) ?? Array.Empty<string>();

    /// <summary>
    /// One agent's state as a line of prose for the model. Deliberately plain text rather than
    /// JSON: it is read by a language model, and the busy/idle distinction is the part that
    /// actually answers "have you finished yet".
    /// </summary>
    public static string Describe(AgentEntry a)
    {
        var parts = new List<string> { $"Agent \"{a.Name}\"", $"working in {a.FolderLabel} ({a.ProjectRoot})", $"model {a.Model}" };
        if (a.PlanTotal > 0) parts.Add($"executing a plan, step {a.PlanStep} of {a.PlanTotal}");
        parts.Add(a.IsBusy ? "currently WORKING on a turn" : "IDLE — not currently working");
        if (a.IsRunningCommand) parts.Add("a shell command is running right now");
        return string.Join("; ", parts) + ".";
    }
}
