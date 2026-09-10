using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Owns the file-backed <see cref="MandoCodeConfig"/> and is the ONLY code in the desktop app
/// that calls <see cref="MandoCodeConfig.Save"/>.
///
/// Settings are per-agent. Each tab runs on its own clone, and editing Settings while looking at
/// an agent changes that agent alone. ~/.mandocode/config.json is not "the current settings"; it is
/// <see cref="Defaults"/>, the starting point every NEW agent is seeded from. "Make Default for New
/// Agents" is the one action that writes it.
///
/// An agent's own settings outlive the process: the first change to one snapshots it to
/// <see cref="AgentConfigStore"/> and it boots on those settings from then on (see
/// <see cref="CreateCloneFor"/>). An agent that has never been configured has no file and is
/// re-seeded from Defaults on every load, so a default raised today still reaches it.
///
/// One thing is deliberately app-wide rather than per-agent, because the machinery underneath
/// it is: MCP servers — OS processes owned by a single shared McpClientManager. An agent can turn
/// MCP on or off for itself (AIService.AttachMcpPluginsAsync honours its own EnableMcp), but
/// the server SET is one list. Edits land on Defaults and mirror into every live agent, so
/// per-agent autoApprove lookups agree with what the MCP page shows.
/// (ContextLength used to be on this list — env-var-scoped to the one daemon — but it now rides
/// on every request as num_ctx, so each agent's own value governs its own conversations.)
///
/// A clone's Save() must never be called: it would write that agent's session settings over
/// everybody's defaults. The method is public and non-virtual on a type in the read-only harness
/// submodule, so the rule can't be enforced by the type system. The MANDO001 build target in
/// MandoCode.Desktop.csproj enforces it instead.
/// </summary>
public sealed class ConfigCoordinator
{
    private readonly object _gate = new();

    /// <summary>What a NEW agent starts on. Not the settings of any agent you're looking at.</summary>
    public MandoCodeConfig Defaults { get; }

    /// <summary>Set by <see cref="SessionManager"/> once both exist (they'd otherwise be a
    /// construction cycle: sessions need the coordinator, MCP mirroring needs the sessions).</summary>
    public Func<IEnumerable<AgentSession>> SessionsAccessor { get; set; } = Array.Empty<AgentSession>;

    public ConfigCoordinator(MandoCodeConfig defaults) => Defaults = defaults;

    /// <summary>A fresh, fully-detached copy of the defaults for a new agent.</summary>
    public MandoCodeConfig CreateClone() => ConfigCloning.DeepClone(Defaults);

    /// <summary>
    /// The config an agent should BOOT on. An agent that has been configured before comes back on
    /// its own saved settings; one that never has is seeded from the current defaults, so raising a
    /// global default still reaches every agent the user never touched.
    ///
    /// Two things are re-sourced from <see cref="Defaults"/> even on a restored config, because
    /// they are app-wide rather than per-agent: the MCP server SET (see
    /// <see cref="SyncMcpServersToAgents"/>) and secrets (see <see cref="ApplySecretsTo"/>, which
    /// is also why the saved file has no key in it to restore).
    /// </summary>
    public MandoCodeConfig CreateCloneFor(string persistKey)
    {
        lock (_gate)
        {
            var restored = AgentConfigStore.TryLoad(persistKey);
            if (restored == null) return ConfigCloning.DeepClone(Defaults);

            restored.McpServers = ConfigCloning.DeepClone(Defaults).McpServers;
            ApplySecretsTo(restored);
            restored.ValidateAndClamp();
            return restored;
        }
    }

    /// <summary>
    /// Copies the app-wide secrets onto one agent's clone. Secrets are deliberately absent from
    /// per-agent files (<see cref="AgentConfigStore"/> strips them), so this is what makes
    /// <c>Config.TavilyApiKey</c> non-null on a restored agent — the value lives in exactly one
    /// file on disk and is fanned out into memory from there.
    /// </summary>
    public void ApplySecretsTo(MandoCodeConfig target) => target.TavilyApiKey = Defaults.TavilyApiKey;

    /// <summary>
    /// Mirrors a changed secret into every live agent — the same fan-out
    /// <see cref="SyncMcpServersToAgents"/> does, for the same reason: the value is app-wide, so an
    /// agent holding a stale copy would quietly use the old key.
    /// </summary>
    public void SyncSecretsToAgents()
    {
        lock (_gate)
        {
            foreach (var session in SessionsAccessor()) ApplySecretsTo(session.Config);
        }
    }

    /// <summary>
    /// The inverse of <see cref="SaveDefaultsFrom"/> — overwrites one agent's live config with the
    /// current defaults, IN PLACE so every collaborator holding the same instance (AIService,
    /// SkillLoader, McpApprovalGate) sees the new values without being rebuilt.
    ///
    /// <see cref="MandoCodeConfig.AgentName"/> is deliberately preserved: it is this tab's spoken
    /// identity rather than a setting, and Defaults never carries one, so copying it across would
    /// blank the agent's name out of its own system prompt.
    /// </summary>
    public void CopyDefaultsOnto(MandoCodeConfig target)
    {
        lock (_gate) ConfigCloning.CopyOnto(Defaults, target);
    }

    /// <summary>
    /// "Make Default for New Agents" — snapshots one agent's settings onto the defaults and
    /// persists them. Agents already open are untouched; they keep their own settings.
    /// </summary>
    public void SaveDefaultsFrom(MandoCodeConfig agentConfig)
    {
        lock (_gate)
        {
            ConfigCloning.CopyOnto(agentConfig, Defaults);
            Defaults.Save();
        }
    }

    /// <summary>
    /// Persists Defaults as they stand. For corrections and app-wide facts rather than
    /// preferences — a healed endpoint URL, the onboarding-complete flag, an MCP server edit.
    /// </summary>
    public void SaveDefaults()
    {
        lock (_gate)
        {
            Defaults.ValidateAndClamp();
            Defaults.Save();
        }
    }

    /// <summary>
    /// MCP servers are one app-wide set. After the MCP page edits <see cref="Defaults"/>, mirror
    /// the list into every live agent's clone so each agent's McpApprovalGate resolves the same
    /// autoApprove entries the page just showed.
    /// </summary>
    public void SyncMcpServersToAgents()
    {
        lock (_gate)
        {
            foreach (var session in SessionsAccessor())
            {
                // One fresh clone per agent — a shared dictionary would let one agent's session
                // approvals mutate another's.
                session.Config.McpServers = ConfigCloning.DeepClone(Defaults).McpServers;
            }
        }
    }
}
