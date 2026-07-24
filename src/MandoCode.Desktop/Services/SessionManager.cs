namespace MandoCode.Desktop.Services;

/// <summary>
/// Owns the set of open agent tabs and which one is active.
///
/// Also closes the construction cycle between the coordinators and the sessions: a session needs
/// <see cref="ConfigCoordinator"/> and <see cref="McpCoordinator"/> to be built, but both need to
/// enumerate live sessions to fan changes out. They're handed this manager's accessor once, here.
/// </summary>
public sealed class SessionManager
{
    private readonly IServiceProvider _globals;
    private readonly ConfigCoordinator _configs;
    private readonly McpCoordinator _mcp;
    private readonly SkillCoordinator _skills;
    private readonly string _initialProjectRoot;
    private readonly List<AgentSession> _sessions = new();

    public IReadOnlyList<AgentSession> Sessions => _sessions;

    /// <summary>
    /// The agent the user is looking at — what Settings, MCP, and Esc act on. Stays put while the
    /// Settings page is open, which is what makes "these settings belong to Agent 2" true.
    /// Null only before the first session is created.
    /// </summary>
    public AgentSession? Active { get; private set; }

    public SessionManager(
        IServiceProvider globals,
        ConfigCoordinator configs,
        McpCoordinator mcp,
        SkillCoordinator skills,
        string initialProjectRoot)
    {
        _globals = globals;
        _configs = configs;
        _mcp = mcp;
        _skills = skills;
        _initialProjectRoot = initialProjectRoot;

        _configs.SessionsAccessor = () => _sessions;
        _mcp.SessionsAccessor = () => _sessions;
        _skills.SessionsAccessor = () => _sessions;
    }

    /// <summary>
    /// Opens a new agent. It inherits the active tab's project folder — a new tab is almost always
    /// "another agent on what I'm already working on" — and the canonical default model.
    /// </summary>
    public AgentSession CreateSession(string? projectRoot = null, string? persistKey = null)
    {
        var root = projectRoot ?? Active?.ProjectRoot.ProjectRoot ?? _initialProjectRoot;
        var session = new AgentSession(_globals, _configs, _mcp, root, persistKey);
        session.Title = NextAgentName();

        _sessions.Add(session);
        Activate(session);
        return session;
    }

    /// <summary>
    /// Default tab label: "Agent 1", "Agent 2", … The folder is shown in each tab's header, so the
    /// label just distinguishes agents; the user can rename it. Reuses the lowest free number so
    /// closing "Agent 2" then opening a new one gives "Agent 2" again, not an ever-climbing count.
    /// </summary>
    private string NextAgentName() => AgentNaming.NextFreeName(_sessions.Select(s => s.Title));

    public void Activate(AgentSession session)
    {
        if (ReferenceEquals(Active, session)) return;
        if (!_sessions.Contains(session)) return;

        Active = session;
    }

    /// <summary>
    /// Closes an agent. Cancels anything it has in flight; the shared MCP servers and music player
    /// are untouched (they belong to the window, not the agent). The caller owns tearing down the
    /// agent's UI — see ChatTabView.Shutdown, which closes its WebView2.
    /// </summary>
    public void CloseSession(AgentSession session)
    {
        var index = _sessions.IndexOf(session);
        if (index < 0) return;

        session.Controller.CancelActiveRequest();
        _sessions.RemoveAt(index);

        if (!ReferenceEquals(Active, session)) return;

        Active = null;
        if (_sessions.Count == 0) return;

        Activate(_sessions[Math.Min(index, _sessions.Count - 1)]);
    }
}
