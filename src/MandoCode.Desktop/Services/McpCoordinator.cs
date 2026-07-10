using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// App-global owner of the MCP server lifecycle.
///
/// The servers are OS processes (stdio) or remote connections held by a single shared
/// <see cref="McpClientManager"/> — one set for the whole app, however many agents are open.
/// Each agent has its own <see cref="McpApprovalGate"/> (so approving a tool in one agent's
/// context doesn't silently authorize another) and its own kernel, which must re-register the
/// shared clients' tools whenever the server set changes.
///
/// That split is why reload can't live in ChatController: it would restart the processes once
/// per agent and refresh only the agent that asked.
/// </summary>
public sealed class McpCoordinator
{
    private readonly MandoCodeConfig _defaults;

    /// <summary>
    /// The config the shared manager runs on. Deliberately NOT the defaults.
    ///
    /// McpClientManager reads exactly two things — <c>EnableMcp</c> and <c>McpServers</c> — and
    /// short-circuits <c>StartAllAsync</c> when <c>EnableMcp</c> is false. But EnableMcp is a
    /// PER-AGENT setting in this app: it decides whether that agent attaches the tools
    /// (AIService.AttachMcpPluginsAsync honours its own copy). Pointing the manager at the
    /// defaults would mean a saved default of <c>enableMcp: false</c> silently starves every
    /// agent that turns MCP on — no servers, no tools, no error.
    ///
    /// Starting the processes is an app-wide job, so the manager gets a config that always says
    /// yes, with the server list mirrored from the defaults before each call.
    /// </summary>
    private readonly MandoCodeConfig _hostConfig;

    private readonly McpClientManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    /// <summary>Set by <see cref="SessionManager"/> — see the note on ConfigCoordinator.</summary>
    public Func<IEnumerable<AgentSession>> SessionsAccessor { get; set; } = Array.Empty<AgentSession>;

    public McpClientManager Manager => _manager;

    public McpCoordinator(MandoCodeConfig defaults)
    {
        _defaults = defaults;
        _hostConfig = new MandoCodeConfig { EnableMcp = true };
        _manager = new McpClientManager(_hostConfig);
    }

    /// <summary>
    /// Re-point the manager at the current server list. The defaults' dictionary reference is
    /// replaced wholesale by ConfigCoordinator.SaveDefaultsFrom, so this can't be wired once.
    /// </summary>
    private void MirrorServers() => _hostConfig.McpServers = _defaults.McpServers;

    /// <summary>
    /// Starts every configured server, once per app run. A new agent calls this and gets a no-op
    /// if the servers are already up; it then attaches their tools to its own kernel via
    /// <c>AIService.AttachMcpPluginsAsync</c>. Only agents that have MCP enabled call it.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        if (_started) return;

        await _gate.WaitAsync();
        try
        {
            if (_started) return;
            MirrorServers();
            await _manager.StartAllAsync();
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restarts the shared servers and re-registers their tools on EVERY open agent's kernel.
    /// Without the fan-out, agents other than the one that ran /mcp-reload keep stale tool
    /// handles. Each agent's session approvals reset, since the tools behind them may have changed.
    /// </summary>
    public async Task ReloadAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var sessions = SessionsAccessor().ToList();

            foreach (var session in sessions)
                session.McpGate.ResetSession();

            MirrorServers();
            await _manager.ReloadAsync();
            _started = true;

            // RefreshSettingsAsync, not ReinitializeAsync: rebuilds the kernel and re-attaches
            // MCP tools while keeping each agent's conversation history. An agent with MCP
            // disabled simply attaches nothing.
            foreach (var session in sessions)
                await session.Ai.RefreshSettingsAsync(session.Config);
        }
        finally
        {
            _gate.Release();
        }
    }
}
