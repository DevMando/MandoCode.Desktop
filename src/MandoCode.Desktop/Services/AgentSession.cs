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

    /// <summary>Tab-strip label. Defaults to the project folder's leaf name.</summary>
    public string Title { get; set; }

    // ---- Per-session graph ----
    public MandoCodeConfig Config { get; }
    public ProjectRootAccessor ProjectRoot { get; }
    public TokenTrackingService Tokens { get; }
    public PlanHandoff PlanHandoff { get; }
    public SkillLoader Skills { get; }
    public McpApprovalGate McpGate { get; }
    public AIService Ai { get; }
    public TaskPlannerService Planner { get; }
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
        string projectRoot)
    {
        Id = Interlocked.Increment(ref _nextId);

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
        Title = FolderLabel(projectRoot);

        Tokens = new TokenTrackingService();
        PlanHandoff = new PlanHandoff();
        Skills = new SkillLoader(Config, ProjectRoot);
        McpGate = new McpApprovalGate(Config);

        Ai = new AIService(ProjectRoot, Config, Tokens, PlanHandoff, Skills, mcpManager, McpGate, spinner);
        Planner = new TaskPlannerService(Ai, Config);

        var ignoreDirs = new HashSet<string>(MandoCodeConfig.DefaultIgnoreDirectories);
        foreach (var dir in Config.IgnoreDirectories) ignoreDirs.Add(dir);
        FileProvider = new FileAutocompleteProvider(ProjectRoot, ignoreDirs);

        Busy = new BusyStateService();
        Transcript = new TranscriptWriter();
        PromptGate = new ApprovalPromptGate();
        Approvals = new WinUiApprovalService(PromptGate, Busy, PlanHandoff, Transcript, html);
        Shell = new ShellRunner(ProjectRoot, Transcript, html);

        Controller = new ChatController(
            Ai, Config, Tokens, PlanHandoff, Planner,
            mcpManager, McpGate, Skills, FileProvider, ProjectRoot,
            music, updateCheck, Approvals, Transcript, html, Busy, Shell, PromptGate,
            configs, mcp, Snapshots);
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
