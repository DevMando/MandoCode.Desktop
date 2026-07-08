using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace MandoCode.Desktop;

/// <summary>
/// WinUI 3 host for the MandoCode harness. Mirrors the CLI's Program.cs service
/// registrations, minus the RazorConsole/terminal-only pieces (translators, console
/// options, key coordinators) and plus the WinUI-side services (approval UI bridge,
/// transcript writer, busy state).
/// </summary>
public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Same process-wide regex backstop as the CLI — must run before any Regex
        // is constructed (see Program.cs in MandoCode for the full rationale).
        AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(10));

        Services = BuildServices();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // ---- Configuration (identical to CLI Program.cs) ----
        var config = MandoCodeConfig.Load();

        var envEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT");
        if (!string.IsNullOrEmpty(envEndpoint)) config.OllamaEndpoint = envEndpoint;

        var envModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL");
        if (!string.IsNullOrEmpty(envModel)) config.ModelName = envModel;

        services.AddSingleton(config);

        // Project root: first command-line arg, else current directory. The UI also
        // has an "Open Folder" action that mutates ProjectRootAccessor at runtime.
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var projectRoot = cmdArgs.Length > 0 && System.IO.Directory.Exists(cmdArgs[0])
            ? System.IO.Path.GetFullPath(cmdArgs[0])
            : Environment.CurrentDirectory;
        services.AddSingleton(new ProjectRootAccessor(projectRoot));

        // ---- Harness services, reused verbatim ----
        // SpinnerService writes ANSI to Console, which is a harmless no-op in a
        // windowed app (no console attached). It's still registered because AIService
        // takes it as a constructor dependency; the real busy UI is BusyStateService.
        services.AddSingleton<SpinnerService>();
        services.AddSingleton<ApprovalPromptGate>();
        services.AddSingleton<TokenTrackingService>();
        services.AddSingleton<PlanHandoff>();

        services.AddSingleton(provider =>
        {
            var cfg = provider.GetRequiredService<MandoCodeConfig>();
            var root = provider.GetRequiredService<ProjectRootAccessor>();
            return new SkillLoader(cfg, root);
        });

        services.AddSingleton(provider => new McpClientManager(provider.GetRequiredService<MandoCodeConfig>()));
        services.AddSingleton(provider => new McpApprovalGate(provider.GetRequiredService<MandoCodeConfig>()));

        services.AddSingleton(provider =>
        {
            var cfg = provider.GetRequiredService<MandoCodeConfig>();
            var tokenTracker = provider.GetRequiredService<TokenTrackingService>();
            var root = provider.GetRequiredService<ProjectRootAccessor>();
            var planHandoff = provider.GetRequiredService<PlanHandoff>();
            var skillLoader = provider.GetRequiredService<SkillLoader>();
            var mcpManager = provider.GetRequiredService<McpClientManager>();
            var mcpGate = provider.GetRequiredService<McpApprovalGate>();
            var spinner = provider.GetRequiredService<SpinnerService>();
            return new AIService(root, cfg, tokenTracker, planHandoff, skillLoader, mcpManager, mcpGate, spinner);
        });

        services.AddSingleton(provider =>
        {
            var ai = provider.GetRequiredService<AIService>();
            var cfg = provider.GetRequiredService<MandoCodeConfig>();
            return new TaskPlannerService(ai, cfg);
        });

        services.AddSingleton(provider => new MusicPlayerService(provider.GetRequiredService<MandoCodeConfig>()));

        services.AddSingleton(provider =>
        {
            var cfg = provider.GetRequiredService<MandoCodeConfig>();
            var ignoreDirs = new HashSet<string>(MandoCodeConfig.DefaultIgnoreDirectories);
            foreach (var dir in cfg.IgnoreDirectories) ignoreDirs.Add(dir);
            var root = provider.GetRequiredService<ProjectRootAccessor>();
            return new FileAutocompleteProvider(root, ignoreDirs);
        });

        // ---- WinUI-side services (replace the terminal rendering layer) ----
        // Update checks hit MandoCode.Desktop's own GitHub releases, not the CLI's NuGet
        // package — the CLI's UpdateCheckService is deliberately NOT registered.
        services.AddSingleton<UiUpdateCheckService>();
        services.AddSingleton<TranscriptWriter>();
        services.AddSingleton<BusyStateService>();
        services.AddSingleton<TranscriptHtmlBuilder>();
        services.AddSingleton<ShellRunner>();
        services.AddSingleton<WinUiApprovalService>();
        services.AddSingleton<ChatController>();

        return services.BuildServiceProvider();
    }
}
