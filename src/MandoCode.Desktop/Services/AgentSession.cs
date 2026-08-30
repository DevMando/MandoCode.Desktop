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

        Config = configs.CreateClone();
        ProjectRoot = new ProjectRootAccessor(projectRoot);
        // Before AIService below: its constructor bakes the system prompt, and the agent's
        // spoken identity (Config.AgentName, stamped by the Title setter) must be in it.
        Title = title ?? FolderLabel(projectRoot);

        Tokens = new TokenTrackingService();
        PlanHandoff = new PlanHandoff();
        Skills = new SkillLoader(Config, ProjectRoot);
        McpGate = new McpApprovalGate(Config);

        Ai = new AIService(ProjectRoot, Config, Tokens, PlanHandoff, Skills, mcpManager, McpGate, spinner);
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
    }

    /// <summary>Repoints this tab at a different project folder and rebuilds its AI session.</summary>
    public async Task ChangeProjectRootAsync(string folder)
    {
        // The tab keeps its name ("Agent N" or a user rename) across a folder change — the folder
        // is shown in the header, so the label doesn't need to track it.
        ProjectRoot.ProjectRoot = folder;
        FileProvider.RefreshCache();
        await Ai.ReinitializeAsync(Config);
    }

    private static string FolderLabel(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
