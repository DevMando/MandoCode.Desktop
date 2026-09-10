using MandoCode.Desktop.ViewModels;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MandoCode.Desktop.Services;

/// <summary>
/// One agent tab: its own conversation, project folder, model, and approval decisions.
///
/// The graph below is hand-constructed rather than resolved from a DI child scope. The harness
/// types take concrete constructor parameters, and a session must MIX app-global singletons
/// (McpClientManager, MusicPlayerService) with fresh per-tab instances while substituting a
/// config clone for the registered canonical one. A scope would mean re-registering fifteen
/// types as Scoped plus a per-scope config override — more machinery than this factory.
///
/// Everything here is safe to instantiate N times: the harness's statics are pure functions and
/// readonly Regex, and AIService takes every collaborator by constructor.
///
/// Three of these MUST be per-session or tabs corrupt each other:
///   • WinUiApprovalService  — holds the "don't ask again" bypass set. Shared, one tab's
///                             blanket approval silently auto-approves writes in every other.
///   • ApprovalPromptGate    — a SemaphoreSlim(1,1) built to serialize prompts on one console.
///                             Shared, an unanswered approval in tab A stops tab B's from ever
///                             rendering, and tab B just looks hung.
///   • McpApprovalGate       — holds session approvals AND a single-assignment
///                             OnApprovalRequested delegate (as do three delegates on AIService
///                             and one on PlanHandoff, all assigned in ChatController's ctor).
///                             Shared, the last tab constructed silently steals every approval.
/// </summary>
public sealed class AgentSession
{
    private static int _nextId;

    public int Id { get; }

    /// <summary>Tab-strip label AND the agent's spoken identity: setting it also stamps
    /// <see cref="MandoCodeConfig.AgentName"/> on this session's config clone, so the system
    /// prompt introduces the agent by this name on the next prompt rebuild (construction,
    /// settings refresh, or model switch). Defaults to the project folder's leaf name.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Config.AgentName = value;
            TitleChanged?.Invoke(value);
        }
    }
    private string _title = "";

    /// <summary>Raised when the user renames this tab's agent.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Durable identity across app launches (unlike <see cref="Id"/>, a process-local
    /// counter). Names this session's transcript journal on disk; a restored tab passes its
    /// saved key back in so it reattaches to its own history.</summary>
    public string PersistKey { get; }

    // ---- Per-session graph ----
    public MandoCodeConfig Config { get; }
    public ProjectRootAccessor ProjectRoot { get; }
    public TokenTrackingService Tokens { get; }
    public PlanHandoff PlanHandoff { get; }
    public SkillLoader Skills { get; }
    public McpApprovalGate McpGate { get; }
    public AIService Ai { get; }
    public TaskPlannerService Planner { get; }
    public PlanRunnerSelector PlanRunners { get; }
    public FileAutocompleteProvider FileProvider { get; }
    public BusyStateService Busy { get; }
    public TranscriptWriter Transcript { get; }
    public ApprovalPromptGate PromptGate { get; }
    public WinUiApprovalService Approvals { get; }
    public ShellRunner Shell { get; }
    public DesktopPreviewTools PreviewTools { get; }
    public ChatController Controller { get; }

    /// <summary>App-wide snapshot store, shared with every other tab (see <see cref="SnapshotStore"/>).</summary>
    public SnapshotStore Snapshots { get; }

    public AgentSession(
        IServiceProvider globals,
        ConfigCoordinator configs,
        McpCoordinator mcp,
        string projectRoot,
        string? persistKey = null,
        string? title = null)
    {
        Id = Interlocked.Increment(ref _nextId);
        PersistKey = string.IsNullOrWhiteSpace(persistKey) ? Guid.NewGuid().ToString("N") : persistKey;

        // Globals — shared with every other tab. The MCP manager comes from the coordinator that
        // owns it, not from DI, so every agent talks to the one set of server processes.
        var html = globals.GetRequiredService<TranscriptHtmlBuilder>();
        var spinner = globals.GetRequiredService<SpinnerService>();
        var mcpManager = mcp.Manager;
        var music = globals.GetRequiredService<MusicPlayerService>();
        var updateCheck = globals.GetRequiredService<UiUpdateCheckService>();
        Snapshots = globals.GetRequiredService<SnapshotStore>();

        // Boots on this agent's OWN saved settings if it has ever been configured, otherwise on a
        // fresh clone of the defaults. Must be before AIService below, which bakes the system
        // prompt (and reads the model) in its constructor.
        Config = configs.CreateCloneFor(PersistKey);
        _configs = configs;
        _persistedConfigJson = AgentConfigStore.Fingerprint(Config);
        ProjectRoot = new ProjectRootAccessor(projectRoot);
        // Before AIService below: its constructor bakes the system prompt, and the agent's
        // spoken identity (Config.AgentName, stamped by the Title setter) must be in it.
        Title = title ?? FolderLabel(projectRoot);

        Tokens = new TokenTrackingService();
        PlanHandoff = new PlanHandoff();
        Skills = new SkillLoader(Config, ProjectRoot);
        McpGate = new McpApprovalGate(Config);

        Ai = new AIService(ProjectRoot, Config, Tokens, PlanHandoff, Skills, mcpManager, McpGate, spinner);
        PreviewTools = new DesktopPreviewTools(ProjectRoot) { RequireTabId = true, ImageSink = new AgentImageSink(new AiServiceAdapter(Ai)) };
        Ai.SetHostTools([
            Microsoft.Extensions.AI.AIFunctionFactory.Create(PreviewTools.ListBrowserFrames, new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "list_browser_frames" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(PreviewTools.ListBrowserTabs, new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "list_browser_tabs" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(PreviewTools.OpenBrowserTab, new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "open_browser_tab" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.OpenDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "open_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.OpenLocalServerDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "open_local_server_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.RefreshDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "refresh_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.ScreenshotDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "screenshot_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.InspectDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "inspect_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.ObserveDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "observe_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.ClickDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "click_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.PressKeyDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "press_key_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.HoverDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "hover_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.FillDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "fill_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.SelectDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "select_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.ScrollDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "scroll_desktop_preview" }),
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                PreviewTools.WaitForDesktopPreview,
                new Microsoft.Extensions.AI.AIFunctionFactoryOptions { Name = "wait_for_desktop_preview" }),
        ]);
        Planner = new TaskPlannerService(Ai, Config);
        // PersistKey is the durable tab identity. Including it in the checkpoint key prevents two
        // agents working in the same project from overwriting each other's unfinished plans.
        PlanRunners = new PlanRunnerSelector(
            Config,
            new AiServicePlanStepExecutor(Ai),
            PlanHandoff,
            ProjectRoot,
            PersistKey);

        var ignoreDirs = new HashSet<string>(MandoCodeConfig.DefaultIgnoreDirectories);
        foreach (var dir in Config.IgnoreDirectories) ignoreDirs.Add(dir);
        FileProvider = new FileAutocompleteProvider(ProjectRoot, ignoreDirs);

        Busy = new BusyStateService();
        Transcript = new TranscriptWriter();
        // Journal every transcript block as it's written (tier-2 session persistence).
        // /clear also clears the on-disk history — cleared means cleared, both files.
        Transcript.BlockAdded += htmlBlock => TranscriptJournal.Append(PersistKey, htmlBlock);
        Transcript.Cleared += () =>
        {
            TranscriptJournal.Delete(PersistKey);
            ConversationLog.Delete(PersistKey);
            SessionHistoryStore.Delete(PersistKey);
        };
        PromptGate = new ApprovalPromptGate();
        Approvals = new WinUiApprovalService(PromptGate, Busy, PlanHandoff, Transcript, html);
        Shell = new ShellRunner(ProjectRoot, Transcript, html);

        Controller = new ChatController(
            new AiServiceAdapter(Ai), Config, Tokens, PlanHandoff, Planner, PlanRunners,
            mcpManager, McpGate, Skills, FileProvider, ProjectRoot,
            music, updateCheck, Approvals, Transcript, html, Busy, Shell, PromptGate,
            configs, mcp, Snapshots);

        // Tier-3 persistence: plain-text turns feed the ConversationLog so a restored
        // session can re-brief the model.
        Controller.ConversationLogger = (role, text) => ConversationLog.Append(PersistKey, role, text);

        // Settings persistence: every deliberate config change offers itself here. The fingerprint
        // compare inside makes a no-op change free, so the controller can raise this liberally.
        Controller.ConfigChanged += PersistConfigIfChanged;
    }

    // ---- Per-agent settings persistence ----

    /// <summary>The config as last written to disk (secrets stripped), or — for an agent that has
    /// never been configured — as it looked at boot. Anything that differs from this is a real
    /// change the user made, which is what turns an inheriting agent into an independent one.</summary>
    private string _persistedConfigJson;

    /// <summary>Held for the defaults comparison in <see cref="PersistConfigIfChanged"/>.</summary>
    private readonly ConfigCoordinator _configs;

    /// <summary>
    /// False until the tab's restore cascade has finished. Restore itself moves the config (a saved
    /// model is applied AFTER construction), and persisting inside that window is the same trap
    /// SaveWorkspace guards against: a tab still sitting on the default would stamp the default
    /// over the user's real choice. MainWindow arms this once the tab has settled.
    /// </summary>
    public bool ConfigPersistenceArmed { get; set; }

    /// <summary>
    /// Writes this agent's settings if they have actually moved. Cheap enough to call on any
    /// checkpoint — it serializes ~4KB and compares, and does no I/O when nothing changed.
    /// </summary>
    public void PersistConfigIfChanged()
    {
        if (!ConfigPersistenceArmed) return;

        try
        {
            // Serializing a config that another thread is mid-mutation on can throw (a collection
            // modified during enumeration). ConfigChanged reaches here off the UI thread — from
            // ApplyConnectionSettingsAsync and model switches — so this must never be the thing
            // that takes the app down. Settings are best-effort persistence, like every sibling
            // store; the next change writes them.
            var current = AgentConfigStore.Fingerprint(Config);

            // An agent whose settings are IDENTICAL to the defaults is an inheriting agent, however
            // it got there — there is nothing to remember that re-reading the defaults wouldn't
            // give back, and staying independent would only mean silently missing future changes.
            //
            // /setup is why this matters rather than being a nicety: the wizard ends with
            // SaveDefaultsFrom (see ChatController.Wizards — "/setup configures the app, not one
            // agent"), so the agent it ran in matches the defaults exactly. Without this, running
            // the wizard would quietly drop that agent out of inheriting as a side effect.
            if (current == AgentConfigStore.Fingerprint(_configs.Defaults))
            {
                if (AgentConfigStore.Exists(PersistKey)) AgentConfigStore.Delete(PersistKey);
                _persistedConfigJson = current;
                return;
            }

            if (current == _persistedConfigJson) return;

            // Hold the new fingerprint even if the write itself failed: a disk that can't take the
            // file won't be fixed by retrying on every keystroke, and the in-memory settings are
            // still correct either way.
            _persistedConfigJson = current;
            AgentConfigStore.Save(PersistKey, Config);
        }
        catch { }
    }

    /// <summary>
    /// "Match global defaults" — drops this agent's saved settings and puts it back on the current
    /// defaults, inheriting future changes again. The live <see cref="Config"/> is mutated in place
    /// so every collaborator holding a reference to it (AIService, SkillLoader, McpApprovalGate)
    /// sees the new values; the caller still has to rebuild the agent for them to take effect —
    /// see ChatController.RefreshFromConfigAsync.
    /// </summary>
    public void ResetConfigToDefaults(ConfigCoordinator configs)
    {
        AgentConfigStore.Delete(PersistKey);
        configs.CopyDefaultsOnto(Config);
        // Back to inheriting: the fingerprint is the defaults, so no file is written again until
        // the user makes a fresh change.
        _persistedConfigJson = AgentConfigStore.Fingerprint(Config);
    }

    /// <summary>
    /// Repoints this tab at a different project folder, KEEPING the conversation. Changing folders
    /// is a navigation step inside one piece of work — "now look at this repo" — not the start of a
    /// new one, so everything said up to here still applies.
    /// </summary>
    public async Task ChangeProjectRootAsync(string folder)
    {
        // The tab keeps its name ("Agent N" or a user rename) across a folder change — the folder
        // is shown in the header, so the label doesn't need to track it.
        ProjectRoot.ProjectRoot = folder;
        FileProvider.RefreshCache();

        // The new folder brings its own project skills, and the skill index is baked into the
        // system prompt — so the rescan has to happen BEFORE the prompt is recomposed below.
        Skills.Reload();

        // RefreshSettingsAsync, not ReinitializeAsync: both rebuild the system prompt, the agent,
        // and the MCP tool set, but ReinitializeAsync ends in ClearHistoryAsync — which is what
        // used to wipe the conversation on every folder change. The tools themselves need no
        // rebuild to follow the move: they hold the live ProjectRootAccessor mutated above, not a
        // copied path. Same trade as ChatController.RefreshFromConfigAsync.
        await Ai.RefreshSettingsAsync(Config);

        // The model has just been handed a new working folder while still holding a conversation
        // about the old one. Without this it keeps resolving remembered paths against a root that
        // moved out from under it — the history survives, but silently goes stale.
        Ai.AppendUserNote(
            $"[Project root changed to: {folder}. Earlier messages refer to the previous folder — " +
            "re-read any file you need rather than reusing paths or contents from before this point.]");
    }

    private static string FolderLabel(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
