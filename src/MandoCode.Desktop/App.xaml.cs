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

        // Record any unhandled exception with its full stack to crash.log, so a UI-thread throw
        // shows what actually failed instead of only the generated debugger-break in App.g.i.cs.
        UnhandledException += (_, e) => CrashLog.Write("UnhandledException", e.Exception);

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

        // Project root the FIRST tab opens on: first command-line arg, else current directory.
        // Every tab owns its own ProjectRootAccessor thereafter (see AgentSession), so the
        // folder button retargets one tab without disturbing the others.
        var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var projectRoot = cmdArgs.Length > 0 && System.IO.Directory.Exists(cmdArgs[0])
            ? System.IO.Path.GetFullPath(cmdArgs[0])
            : Environment.CurrentDirectory;

        // ---- App-global singletons ----
        // Everything that carries per-conversation state (AIService, ChatController, the three
        // approval gates, TranscriptWriter, BusyStateService, TokenTrackingService, …) is built
        // per tab by AgentSession and is deliberately NOT registered here.
        //
        // SpinnerService writes ANSI to Console, a harmless no-op in a windowed app (no console
        // attached). It's still registered because AIService takes it; the real busy UI is the
        // per-tab BusyStateService.
        services.AddSingleton<SpinnerService>();

        // The single McpClientManager is created and owned by McpCoordinator, not registered here:
        // it must run on a config that always has MCP enabled, because EnableMcp is a per-agent
        // setting in this app. See the note on McpCoordinator._hostConfig.

        // One audio device.
        services.AddSingleton(provider => new MusicPlayerService(provider.GetRequiredService<MandoCodeConfig>()));

        // ---- WinUI-side services (replace the terminal rendering layer) ----
        // Update checks hit MandoCode.Desktop's own GitHub releases, not the CLI's NuGet
        // package — the CLI's UpdateCheckService is deliberately NOT registered.
        services.AddSingleton<UiUpdateCheckService>();
        services.AddSingleton<TranscriptHtmlBuilder>();   // stateless formatter

        // App-wide store of context snapshots taken when a tab switches its model, so any tab can
        // re-import a conversation captured by another. Session-scoped, not persisted.
        services.AddSingleton<SnapshotStore>();

        // App-wide index of closed conversations, so a tab you close can be reopened from the
        // History panel instead of being lost. Persisted; the journals it points at are the same
        // per-key stores a live tab uses.
        services.AddSingleton<SessionArchiveStore>();

        // ---- Coordinators + session registry ----
        services.AddSingleton(provider => new ConfigCoordinator(provider.GetRequiredService<MandoCodeConfig>()));
        services.AddSingleton(provider => new McpCoordinator(provider.GetRequiredService<MandoCodeConfig>()));
        services.AddSingleton(provider => new SkillCoordinator(provider.GetRequiredService<MandoCodeConfig>()));
        services.AddSingleton(provider => new SessionManager(
            provider,
            provider.GetRequiredService<ConfigCoordinator>(),
            provider.GetRequiredService<McpCoordinator>(),
            provider.GetRequiredService<SkillCoordinator>(),
            projectRoot));

        return services.BuildServiceProvider();
    }
}
