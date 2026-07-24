using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SessionManager _sessions;
    private readonly SnapshotStore _snapshotStore;   // app-wide context snapshots
    private readonly SessionArchiveStore _archive;   // app-wide index of closed conversations
    private readonly SkillCoordinator _skillCoordinator;   // app-wide global-skills manager
    private readonly ConfigCoordinator _configs;   // owns the app-wide MCP server list (defaults)
    private readonly TranscriptHtmlBuilder _html;   // app-global, stateless formatter
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    // Snapshots and History share the one docked column left of the content (Grid.Column 1) and are
    // mutually exclusive — opening one swaps out the other without re-sliding the column.
    private bool _snapshotsPanelOpen;
    private bool _historyPanelOpen;

    // Slide animation state for the docked left column. The width is tweened per-frame off
    // CompositionTarget.Rendering so the panel glides in/out instead of snapping. Width is always
    // held in pixels during and after the tween (never star) so an interrupted open/close can read
    // the current width and continue smoothly from wherever it is.
    private readonly Stopwatch _snapAnimClock = new();
    private EventHandler<object>? _snapAnimHandler;
    private double _snapAnimFrom, _snapAnimTo;
    private FrameworkElement? _snapAnimHide;   // panel to collapse when a close tween completes
    private const double SnapAnimDurationMs = 220;

    /// <summary>
    /// Settings and MCP edit the app-global config, but they still need a controller to route
    /// through — it owns ConfigKeySetter, the MCP coordinator, and a transcript to report into.
    /// The active chat agent supplies one. There is always at least one chat tab.
    /// </summary>
    private ChatController _controller => _sessions.Active!.Controller;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MandoCode Desktop";

        ThemeManager.Initialize(Root);
        // ONE window-level subscription to the static ThemeChanged event. Chat tabs must not
        // subscribe individually — the handler would outlive every closed tab and leak.
        ThemeManager.ThemeChanged += () => OnUi(ApplyThemeToAllTabs);
        SettingsTabs.SelectedItem = Tab_Model;   // the setup that matters most opens first
        ThemeList.ItemsSource = UiTheme.All.Select(t => new ThemeVm { Theme = t }).ToList();
        ThemeHeaderValue.Text = ThemeManager.Current.Name;
        ModelCombo.Loaded += (_, _) => ApplyModelComboTarget();
        S_WindowOpacity.Value = ThemeManager.WindowOpacity * 100;
        S_WindowOpacityLabel.Text = $"{(int)S_WindowOpacity.Value}%";
        ApplyWindowOpacity(ThemeManager.WindowOpacity);
        S_BgOpacity.Value = ThemeManager.ChatBackgroundOpacity * 100;
        S_BgOpacityLabel.Text = $"{(int)S_BgOpacity.Value}%";
        BoxedMessagesToggle.IsOn = ThemeManager.BoxedMessages;
        UpdateBgControls();
        _appearanceReady = true;   // opacity handlers may persist from here on

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var services = App.Services;
        _html = services.GetRequiredService<TranscriptHtmlBuilder>();
        _sessions = services.GetRequiredService<SessionManager>();
        _snapshotStore = services.GetRequiredService<SnapshotStore>();
        _archive = services.GetRequiredService<SessionArchiveStore>();
        _skillCoordinator = services.GetRequiredService<SkillCoordinator>();
        _configs = services.GetRequiredService<ConfigCoordinator>();
        // Changed can fire on a background thread (a capture during a model switch).
        _snapshotStore.Changed += () => OnUi(OnSnapshotsChanged);
        _archive.Changed += () => OnUi(OnArchiveChanged);

        // Remembered fold state + "last seen" watermarks for both panels (persisted UI preference).
        var panelState = PanelState.Load();
        foreach (var p in panelState.CollapsedSnapshotGroups) _collapsedSnapshotGroups.Add(p);
        foreach (var p in panelState.CollapsedHistoryGroups) _collapsedHistoryGroups.Add(p);
        _snapshotsSeenAt = panelState.SnapshotsSeenAt;
        _historySeenAt = panelState.HistorySeenAt;

        // The first agent. Its whole service graph — AIService, approvals, transcript, token
        // tracking — belongs to it alone, so opening a second tab can't disturb it.
        // Reopen the previous workspace shape (tabs, roots, models, active tab). Folders
        // that no longer exist are skipped; no saved shape (or nothing usable) means the
        // classic single default tab.
        var shape = WorkspaceState.TryLoad();
        if (shape != null)
        {
            foreach (var t in shape.Tabs.Where(t => Directory.Exists(t.ProjectRoot)))
                CreateChatTab(t.ProjectRoot, t.Title, t.Model, t.Key);
            if (_tabs.Count == 0) CreateChatTab();
            else SelectTab(_tabs[Math.Clamp(shape.ActiveIndex, 0, _tabs.Count - 1)]);
        }
        else
        {
            CreateChatTab();
        }

        // Journals whose sessions no longer exist (tabs closed during a crash, pruned
        // folders) have nothing to replay into — clean them up. Archived (closed-but-recoverable)
        // sessions are kept: their files back the History panel, so their keys join the keep-set.
        var liveKeys = _tabs.Select(t => t.View.Session.PersistKey).Concat(_archive.Keys).ToList();
        TranscriptJournal.Sweep(liveKeys);
        ConversationLog.Sweep(liveKeys);
        SessionHistoryStore.Sweep(liveKeys);

        // Size the window; defer WebView2 + harness init until the tree is loaded.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 840));
        // Title-bar icon. The exe already embeds the same .ico (taskbar/Alt-Tab/Explorer via
        // <ApplicationIcon>); this sets the little glyph in the window's own title bar. Best-effort.
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "images", "mandocode-desktop.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch { /* a missing/locked icon must never stop the window from opening */ }
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;

    }

    /// <summary>
    /// Shared resources belong to the window, not to any one agent. Before this, only /exit
    /// disposed the music player — closing with the X leaked the audio device, and no path at
    /// all closed the agents' WebView2s.
    /// </summary>
    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SaveWorkspace();   // capture the shape BEFORE teardown starts mutating state
        foreach (var tab in _tabs) tab.View.Shutdown();
        _terminal?.ShutDown();   // kill any ConPTY shells so no processes leak

        try { App.Services.GetRequiredService<MandoCode.Services.MusicPlayerService>().Dispose(); }
        catch { /* nothing playing, or already disposed */ }
    }

}
