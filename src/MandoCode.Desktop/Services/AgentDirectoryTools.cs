using System.ComponentModel;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Cross-agent tools, exposed to the model through <c>SetHostTools</c> — the same seam the browser
/// preview tools use. Host-owned rather than engine-owned because only the host knows what other
/// agents exist; an agent's own tools stay bounded to its own project root.
///
/// <para>Everything here is OBSERVATION. Nothing in this class runs a turn on another agent's model,
/// which is why none of it can loop, none of it collides with another agent being busy, and none of
/// it raises an approval question. Asking another agent a real question is a separate tool with a
/// genuinely different risk profile — see docs/agent-mentions.md.</para>
/// </summary>
public sealed class AgentDirectoryTools
{
    private readonly AgentDirectory _directory;
    private readonly string _selfKey;
    private readonly Func<string> _selfNameProvider;

    /// <summary><paramref name="selfName"/> is a callback rather than a string because an agent can
    /// be renamed while open, and a stale name would attribute its questions to someone else.</summary>
    /// <summary><paramref name="startDelegation"/> launches the background turn. Injected rather
    /// than called directly so the tools stay free of the threading, and so a test can observe that
    /// a delegation was started without one actually running.</summary>
    public AgentDirectoryTools(AgentDirectory directory, string selfKey, Func<string>? selfName = null,
                               Action<Delegation, IAgentPeer>? startDelegation = null)
    {
        _directory = directory;
        _selfKey = selfKey;
        _selfNameProvider = selfName ?? (() => "another agent");
        _start = startDelegation ?? ((_, _) => { });
    }

    private readonly Action<Delegation, IAgentPeer> _start;

    private string _selfName => _selfNameProvider();

    [Description("Lists the other agents currently open, with the folder each is working in. " +
                 "Use this when the user mentions another agent by name (for example '@Ninja') so you " +
                 "know who they mean and whether that agent exists.")]
    public string ListAgents()
    {
        var others = _directory.All.Where(a => a.Key != _selfKey).ToList();
        if (others.Count == 0)
            return "No other agents are open. Only this one.";


        return "Other open agents:\n" + string.Join("\n",
            others.Select(a => $"- {a.Name} — {a.FolderLabel}{(a.IsBusy ? " (working)" : " (idle)")}"));
    }

    [Description("Reports what another agent is doing right now: whether it is working or idle, how " +
                 "far through a plan it is, and whether a shell command is running. Answers questions " +
                 "like 'has Ninja finished yet' or 'what is Ninja working on'. Reads live state only — " +
                 "it does not interrupt that agent or make it do anything.")]
    public string GetAgentStatus(
        [Description("The agent's name, as the user typed it after '@'. Case does not matter.")] string name)
    {
        var agent = _directory.Resolve(name);
        if (agent == null)
        {
            var open = _directory.All.Where(a => a.Key != _selfKey).Select(a => a.Name).ToList();
            return open.Count == 0
                ? $"No agent named \"{name}\" is open, and there are no other agents open at all."
                : $"No agent named \"{name}\" is open. Currently open: {string.Join(", ", open)}.";
        }

        // Answering about yourself would be a confusing way to learn nothing.
        if (agent.Key == _selfKey)
            return $"\"{name}\" is this agent — you. Answer from what you already know rather than looking yourself up.";

        return AgentDirectory.Describe(agent);
    }

    [Description("Asks another agent a question and returns its answer in its own words. The other " +
                 "agent answers from what it has been working on and can use its own tools to check, " +
                 "so prefer this over reading its transcript when you want a specific answer. Only " +
                 "works when that agent is idle: if it is busy, this returns a note saying so, and you " +
                 "should call get_agent_status and read_agent_transcript instead to work it out from " +
                 "what it has already done.")]
    public async Task<string> AskAgent(
        [Description("The agent's name, as the user typed it after '@'.")] string name,
        [Description("The question, phrased as you would ask a colleague. Be specific — the other " +
                     "agent cannot see your conversation.")] string question)
    {
        var agent = _directory.Resolve(name);
        if (agent == null) return NotFound(name);
        if (agent.Key == _selfKey) return $"\"{name}\" is you. Answer from what you already know.";
        if (string.IsNullOrWhiteSpace(question)) return "Ask an actual question.";

        var peer = _directory.Peer(agent.Key);
        if (peer == null) return $"{agent.Name}'s tab has closed — it cannot be asked anything now.";

        // The loop guard runs before the attempt, since a chain limit is a reason not to ask at all
        // rather than something the target should be woken to discover.
        if (AgentCallChain.Reject(agent.Key, agent.Name) is { } rejection) return rejection;

        // No busy check here. The peer claims itself atomically and reports whether it could —
        // checking first and asking second left a gap where the agent could take a turn in between,
        // and produced a second, worse-worded refusal from the far side. One decision, one place;
        // this layer only chooses how to say it, because only this layer knows the directory.
        var result = await peer.AskAsync(_selfName, question);
        if (result.Answered) return $"{agent.Name} replied:\n{result.Text}";

        return $"{agent.Name} could not answer ({result.Text}). " +
               AgentDirectory.Describe(agent) +
               $" Use read_agent_transcript(\"{agent.Name}\") to work out what it has been doing.";
    }

    [Description("Reads the recent conversation from another agent's tab, so you can work out what " +
                 "it has been doing. Use this when that agent is busy and cannot answer, or when an " +
                 "answer was not enough detail. Returns the most recent turns, oldest first.")]
    public string ReadAgentTranscript(
        [Description("The agent's name, as the user typed it after '@'.")] string name,
        [Description("How many recent turns to return. Defaults to 12; more costs more context.")] int turns = 12)
    {
        var agent = _directory.Resolve(name);
        if (agent == null) return NotFound(name);
        if (agent.Key == _selfKey) return $"\"{name}\" is you — this is your own conversation.";

        // Read from the on-disk log rather than the live session: it needs no reference to the other
        // tab and works while that agent is mid-turn, which is exactly when this gets called.
        var log = ConversationLog.Load(agent.Key);
        if (log.Count == 0)
            return $"{agent.Name} has no conversation yet. " + AgentDirectory.Describe(agent);

        var recent = log.TakeLast(Math.Clamp(turns, 1, 40)).ToList();
        var body = string.Join("\n\n", recent.Select(t => $"[{ConversationLog.RoleLabel(t.R)}] {Trim(t.T, 1_500)}"));
        return $"{agent.Name}'s recent conversation ({recent.Count} of {log.Count} turns):\n\n{body}";
    }

    [Description("Hands a JOB to another agent and returns immediately, without waiting for it to " +
                 "finish. Use this instead of ask_agent whenever the work will take a while — building " +
                 "something, running a long task — so you stay free to keep talking to the user. You " +
                 "will be told automatically when it finishes, and check_delegations tells you how far " +
                 "along it is at any point. Use ask_agent instead for a quick question you need " +
                 "answered right now.")]
    public string DelegateToAgent(
        [Description("The agent's name, as the user typed it after '@'.")] string name,
        [Description("The job, described the way you would brief a colleague. The other agent cannot " +
                     "see your conversation, so include everything it needs.")] string task)
    {
        var agent = _directory.Resolve(name);
        if (agent == null) return NotFound(name);
        if (agent.Key == _selfKey) return $"\"{name}\" is you. Do the work yourself.";
        if (string.IsNullOrWhiteSpace(task)) return "Describe the job first.";

        var peer = _directory.Peer(agent.Key);
        if (peer == null) return $"{agent.Name}'s tab has closed — it cannot be given work now.";

        // Refused rather than queued: a queued job would sit invisibly behind work of unknown
        // length, and the user would be told their request was accepted when nothing had started.
        if (peer.IsBusy)
            return $"{agent.Name} is busy and cannot take this on right now. " +
                   AgentDirectory.Describe(agent) + " Try again once it is idle.";

        if (AgentCallChain.Reject(agent.Key, agent.Name) is { } rejection) return rejection;

        var delegation = _directory.Delegations.Open(_selfKey, _selfName, agent.Key, agent.Name, task);
        _start(delegation, peer);

        return $"Handed to {agent.Name}: {task}\n" +
               "It is working on this now. You will be told when it finishes — carry on with the user " +
               "in the meantime, and use check_delegations if they ask how it is going.";
    }

    [Description("Reports how the jobs you handed to other agents are going — what each one is doing " +
                 "right now, how far through it is, and whether it has finished. Costs nothing and " +
                 "works while those agents are busy, so prefer it over asking them directly.")]
    public string CheckDelegations()
    {
        var mine = _directory.Delegations.OpenedBy(_selfKey);
        if (mine.Count == 0) return "You have not handed any work to another agent.";

        return string.Join("\n\n", mine.Select(d =>
        {
            var peer = _directory.All.FirstOrDefault(a => a.Key == d.ToKey);
            var digest = DelegationRegistry.Digest(d, peer, _directory.RecentCommandsFor(d.ToKey));
            return $"{digest.Subject}\n{digest.Body}";
        }));
    }

    private string NotFound(string name)
    {
        var open = _directory.All.Where(a => a.Key != _selfKey).Select(a => a.Name).ToList();
        return open.Count == 0
            ? $"No agent named \"{name}\" is open, and there are no other agents open at all."
            : $"No agent named \"{name}\" is open. Currently open: {string.Join(", ", open)}.";
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + " … [turn truncated]";
}
