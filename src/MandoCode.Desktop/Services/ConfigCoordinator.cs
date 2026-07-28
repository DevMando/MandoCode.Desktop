using System.Reflection;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Owns the file-backed <see cref="MandoCodeConfig"/> and is the ONLY code in the desktop app
/// that calls <see cref="MandoCodeConfig.Save"/>.
///
/// Settings are per-agent. Each tab runs on its own clone, and editing Settings while looking at
/// an agent changes that agent alone — live, for this session only. The file on disk is not "the
/// current settings"; it is <see cref="Defaults"/>, the starting point every NEW agent is seeded
/// from. "Make Default for New Agents" is the one action that writes it.
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
    // Reflected once. Using reflection rather than a hand-written field list means a config key
    // added by the CLI harness is carried by "Make Default" without a change here.
    private static readonly PropertyInfo[] SettableProperties = typeof(MandoCodeConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

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
    /// "Make Default for New Agents" — snapshots one agent's settings onto the defaults and
    /// persists them. Agents already open are untouched; they keep their own settings.
    /// </summary>
    public void SaveDefaultsFrom(MandoCodeConfig agentConfig)
    {
        lock (_gate)
        {
            // Deep-clone first: reflection assigns reference-typed members straight across, and
            // Defaults must not end up sharing the agent's List/Dictionary instances.
            var snapshot = ConfigCloning.DeepClone(agentConfig);
            foreach (var property in SettableProperties)
                property.SetValue(Defaults, property.GetValue(snapshot));

            Defaults.ValidateAndClamp();
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
