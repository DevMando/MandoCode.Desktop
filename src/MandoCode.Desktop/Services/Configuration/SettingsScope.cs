using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// What a <see cref="Controls.SettingsForm"/> is editing. The form's controls, layout, validation
/// and staging are identical whichever it is — only where a save LANDS differs — so the form is
/// built once and hosted twice rather than duplicated.
///
///   • <see cref="DefaultsSettingsScope"/> — the rail page. Commits to ConfigCoordinator.Defaults:
///     what every NEW agent is seeded from. Nothing live is reconfigured.
///   • <see cref="AgentSettingsScope"/> — the pane behind an agent's gear. Commits to that agent's
///     own config, reconfigures it live, and persists it (see <see cref="AgentConfigStore"/>).
///
/// The form never writes through this interface control-by-control: it edits a DRAFT clone and
/// hands the whole thing to <see cref="CommitAsync"/> when the user presses Save. An abandoned
/// draft is simply dropped.
/// </summary>
public interface ISettingsScope
{
    /// <summary>The live config being edited. Read-only from the form's side — it is the baseline
    /// the draft is diffed against, not something the form mutates.</summary>
    MandoCodeConfig Config { get; }

    /// <summary>Applies the whole draft and returns a line for the status area.</summary>
    Task<string> CommitAsync(MandoCodeConfig draft);

    /// <summary>Pulled models for the picker, or an empty list if Ollama can't be reached.</summary>
    Task<List<string>> ListModelsAsync();

    /// <summary>True for the defaults scope only. App-wide settings — the Tavily key, agent
    /// callsigns, the guided wizard — belong to the whole app rather than to one agent, so they
    /// appear once, on the page that owns app-wide things.</summary>
    bool ShowsAppWideSettings { get; }

    /// <summary>The sentence under the title telling the user what these settings govern.</summary>
    string ScopeDescription { get; }

    /// <summary>
    /// Config keys (JSON property names) this form does not own — edited on some other surface, so
    /// they are neither counted as pending changes nor written by a save. The live value wins over
    /// whatever the draft was cloned with, which is what stops a long-open form from undoing a
    /// change made elsewhere while it sat there.
    /// </summary>
    IReadOnlyList<string> KeysOwnedElsewhere { get; }
}

/// <summary>The rail page: the starting point every new agent is seeded from.</summary>
public sealed class DefaultsSettingsScope(ConfigCoordinator configs) : ISettingsScope
{
    public MandoCodeConfig Config => configs.Defaults;
    public bool ShowsAppWideSettings => true;

    public string ScopeDescription =>
        "The starting point for every new agent. Changing these does not touch an agent you have "
        + "already configured — open that agent's own settings from its gear icon.";

    /// <summary>MCP servers have their own rail page.</summary>
    public IReadOnlyList<string> KeysOwnedElsewhere { get; } = ["mcpServers"];

    public async Task<string> CommitAsync(MandoCodeConfig draft)
    {
        // The MCP page may have edited the server set while this form sat open. It is app-wide and
        // not ours to write, so the live list wins over the draft's snapshot of it.
        draft.McpServers = configs.Defaults.McpServers;
        ConfigCloning.CopyOnto(draft, configs.Defaults);
        configs.SaveDefaults();
        // The Tavily key rides in the draft and is app-wide, so a save here has to reach the agents
        // already open — they hold their own in-memory copy (AgentConfigStore never persists it).
        configs.SyncSecretsToAgents();

        // Probed, not connected: nothing here is talking to a model, but silently saving an endpoint
        // that isn't there would only surface later as a broken agent.
        var probe = await OllamaSetupHelper.ProbeAsync(configs.Defaults.OllamaEndpoint);
        return probe.Ok
            ? $"✓ Saved — new agents will start on {configs.Defaults.GetEffectiveModelName()}."
            : $"Saved, but couldn't reach Ollama at {configs.Defaults.OllamaEndpoint}. New agents will try anyway.";
    }

    public async Task<List<string>> ListModelsAsync()
    {
        try
        {
            var probe = await OllamaSetupHelper.ProbeAsync(configs.Defaults.OllamaEndpoint);
            if (!probe.Ok) return new List<string>();
            return await OllamaSetupHelper.ListModelsAsync(probe.NormalizedUrl);
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>One agent's own settings, edited live from the pane behind its gear icon.</summary>
public sealed class AgentSettingsScope(AgentSession session, ConfigCoordinator configs) : ISettingsScope
{
    public AgentSession Session => session;
    public ConfigCoordinator Configs => configs;
    public MandoCodeConfig Config => session.Config;
    public bool ShowsAppWideSettings => false;

    /// <summary>
    /// Beyond the shared MCP list: an agent's endpoint is set when it launches, and its model comes
    /// from the dropdown in its own header. Neither is editable on this pane, so neither may be
    /// written back by it — otherwise switching models from the header while the pane sat open
    /// would be silently undone by the next save here.
    /// </summary>
    public IReadOnlyList<string> KeysOwnedElsewhere { get; } =
        ["mcpServers", "ollamaEndpoint", "modelName", "modelPath"];

    public string ScopeDescription => AgentConfigStore.Exists(session.PersistKey)
        ? "These settings belong to this agent and are saved — it keeps them when you close and "
        + "reopen it, and changes to the defaults for new agents no longer reach it."
        : "This agent is still on the defaults for new agents. Save a change here and the settings "
        + "become its own, kept and restored with it from then on.";

    public Task<List<string>> ListModelsAsync() => session.Controller.ListModelsAsync();

    public async Task<string> CommitAsync(MandoCodeConfig draft)
    {
        var controller = session.Controller;

        // Everything this pane doesn't own comes back from the live config, so a save here writes
        // only what the user could actually see and change.
        draft.McpServers = Config.McpServers;
        draft.OllamaEndpoint = Config.OllamaEndpoint;
        draft.ModelName = Config.ModelName;
        draft.ModelPath = Config.ModelPath;

        // In place: AIService, SkillLoader and McpApprovalGate all hold this same instance.
        ConfigCloning.CopyOnto(draft, Config);

        // Kernel rebuild rather than reinitialize: nothing reachable from this pane can change the
        // endpoint or model any more, so there is never a reconnect to do and the conversation
        // always survives a save.
        await controller.RefreshFromConfigAsync();
        var message = "✓ Saved — applies from this agent's next message.";

        // RefreshFromConfigAsync doesn't raise ConfigChanged (it isn't a user edit), so persistence
        // is asked for explicitly here. Idempotent: it no-ops when nothing actually moved, and drops
        // the agent's file entirely if the save left it identical to the defaults.
        session.PersistConfigIfChanged();
        return message;
    }
}
