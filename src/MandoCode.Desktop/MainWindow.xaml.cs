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
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

/// <summary>Row model for the slash-command suggestions list.</summary>
public sealed class CommandSuggestion
{
    public string Command { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Row model for the snapshot summarizer dropdown — a model name plus whether it's a cloud
/// model (which may spend tokens) or a local one (free).</summary>
public sealed record ModelChoice(string Name, bool IsCloud)
{
    public string Tag => IsCloud ? "cloud · uses tokens" : "local · free";
}

/// <summary>Row model for diff lines shown in the approval overlay.</summary>
public sealed class DiffLineVm
{
    public string Text { get; init; } = "";
    public SolidColorBrush Brush { get; init; } = new(Colors.Gray);
}

/// <summary>Row model for the MCP servers page.</summary>
public sealed class McpRow
{
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Status { get; init; } = "";
    public SolidColorBrush StatusBrush { get; init; } = new(Colors.Gray);
}

/// <summary>Chip model for the MCP editor's tool preview (test results).</summary>
public sealed class ToolChip
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Row model for the Appearance tab's theme picker — each card is drawn in
/// its own theme's colors so the list doubles as a preview.</summary>
public sealed class ThemeVm
{
    public required UiTheme Theme { get; init; }
    public string Name => Theme.Name;
    public string Description => Theme.Description;
    public SolidColorBrush BgBrush => new(ThemeManager.C(Theme.Background));
    public SolidColorBrush EdgeBrush => new(ThemeManager.C(Theme.Border));
    public SolidColorBrush FgBrush => new(ThemeManager.C(Theme.Text));
    public SolidColorBrush DimBrush => new(ThemeManager.C(Theme.Dim));
    public SolidColorBrush AccentBrush => new(ThemeManager.C(Theme.Accent));
    public SolidColorBrush GoldBrush => new(ThemeManager.C(Theme.Gold));
    public SolidColorBrush SkyBrush => new(ThemeManager.C(Theme.Sky));
    public SolidColorBrush GreenBrush => new(ThemeManager.C(Theme.Green));
}

public sealed partial class MainWindow : Window
{
    private readonly SessionManager _sessions;
    private readonly SnapshotStore _snapshotStore;   // app-wide context snapshots
    private readonly TranscriptHtmlBuilder _html;   // app-global, stateless formatter
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private bool _snapshotsPanelOpen;

    // Slide animation state for the snapshots panel. The column width is tweened per-frame off
    // CompositionTarget.Rendering so the panel glides in/out instead of snapping. Width is always
    // held in pixels during and after the tween (never star) so an interrupted open/close can read
    // the current width and continue smoothly from wherever it is.
    private readonly Stopwatch _snapAnimClock = new();
    private EventHandler<object>? _snapAnimHandler;
    private double _snapAnimFrom, _snapAnimTo;
    private bool _snapAnimHideOnDone;
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
        ModelCombo.Loaded += (_, _) => ApplyModelComboTarget();
        S_WindowOpacity.Value = ThemeManager.WindowOpacity * 100;
        ApplyWindowOpacity(ThemeManager.WindowOpacity);

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var services = App.Services;
        _html = services.GetRequiredService<TranscriptHtmlBuilder>();
        _sessions = services.GetRequiredService<SessionManager>();
        _snapshotStore = services.GetRequiredService<SnapshotStore>();
        // Changed can fire on a background thread (a capture during a model switch).
        _snapshotStore.Changed += () => OnUi(OnSnapshotsChanged);

        // The first agent. Its whole service graph — AIService, approvals, transcript, token
        // tracking — belongs to it alone, so opening a second tab can't disturb it.
        CreateChatTab();

        // Size the window; defer WebView2 + harness init until the tree is loaded.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 840));
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
        foreach (var tab in _tabs) tab.View.Shutdown();

        try { App.Services.GetRequiredService<MandoCode.Services.MusicPlayerService>().Dispose(); }
        catch { /* nothing playing, or already disposed */ }
    }

    private bool _initialized;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _ = _tabs[0].View.InitializeAsync();
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) ActiveChat?.HandleEscape();
    }

    private void ApplyThemeToAllTabs()
    {
        foreach (var tab in _tabs) tab.View.ApplyTheme();
    }

    private void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ============================================================
    // Tabs
    // ============================================================

    /// <summary>One independent agent: its strip header and its chat surface.</summary>
    private sealed class ChatTabEntry
    {
        public required Border Header { get; init; }
        public required TextBlock Label { get; init; }
        public required Ellipse Badge { get; init; }
        public required ChatTabView View { get; init; }
    }

    private readonly List<ChatTabEntry> _tabs = new();
    private ChatTabEntry? _selected;
    private ChatTabEntry? _pendingApprovalTab;
    private bool _approvalToastDismissed;

    /// <summary>
    /// The agent everything else acts on: Esc, the Settings page, the MCP page. Stays put while
    /// you're looking at Settings — that's what makes "these settings belong to Agent 2" true.
    /// </summary>
    private ChatTabView? ActiveChat => _selected?.View;

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        var entry = CreateChatTab();
        _ = entry.View.InitializeAsync();
    }

    private ChatTabEntry CreateChatTab()
    {
        var session = _sessions.CreateSession();
        var view = new ChatTabView(this, session, _html) { Visibility = Visibility.Collapsed };

        view.SetupRequested += () => SwitchPage("settings");
        view.McpEditorRequested += name =>
        {
            SwitchPage("mcp");
            OpenMcpEditor(name);
        };
        view.ClipboardCopyRequested += CopyToClipboard;
        view.ExitRequested += Close;
        view.ApprovalStateChanged += _ => RefreshTabStrip();
        view.HeaderChanged += v =>
        {
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.View, v));
            if (tab != null) tab.Label.Text = v.Session.Title;
        };

        TabHost.Children.Add(view);

        var (header, label, badge) = BuildTabHeader(session.Title);
        var entry = new ChatTabEntry { Header = header, Label = label, Badge = badge, View = view };
        _tabs.Add(entry);
        TabStrip.Children.Add(header);
        WireHeader(entry);

        SelectTab(entry);
        return entry;
    }

    // ============================================================
    // Sidebar navigation — Settings and MCP are full-screen pages, not tabs. They act on
    // whichever agent is selected, so switching pages never changes which agent that is.
    // ============================================================

    private string _currentPage = "chat";

    private void NavChat_Click(object sender, RoutedEventArgs e) => SwitchPage("chat");

    // Settings/MCP act as toggles: clicking the one you're already on closes it and returns to the
    // last active agent, rather than reloading the page in place.
    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "settings" ? "chat" : "settings");
    private void NavMcp_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "mcp" ? "chat" : "mcp");
    private void NavAppearance_Click(object sender, RoutedEventArgs e)
        => SwitchPage(_currentPage == "appearance" ? "chat" : "appearance");

    private void SwitchPage(string page)
    {
        _currentPage = page;
        var showingChat = page == "chat";

        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        McpPage.Visibility = page == "mcp" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;

        // Glide the full-screen page in from the rail side (translate + fade). Both run on the
        // composition thread, so the whole page slides smoothly regardless of how much it holds.
        if (page == "settings") SlideInPage(SettingsPage, SettingsPageTransform);
        else if (page == "mcp") SlideInPage(McpPage, McpPageTransform);
        else if (page == "appearance") SlideInPage(AppearancePage, AppearancePageTransform);

        // Every agent view stays loaded; only the selected one shows, and only on the chat page.
        // Collapsing rather than removing is what keeps each WebView2's transcript alive.
        foreach (var tab in _tabs)
            tab.View.Visibility = showingChat && ReferenceEquals(tab, _selected)
                ? Visibility.Visible : Visibility.Collapsed;

        RefreshNavIcons();
        // Re-evaluate the approval toast for the new page — leaving the chat can newly "hide" the
        // selected agent's approval, which should now raise the toast (and returning clears it).
        RefreshTabStrip();

        switch (page)
        {
            case "settings":
                LoadSettings();
                _ = RefreshModelListAsync();
                break;
            case "mcp":
                _ = RefreshMcpListAsync();
                break;
            default:
                ActiveChat?.FocusInput();
                break;
        }
    }

    /// <summary>Slides a full-screen page (Settings/MCP) into view from the rail side, with a short
    /// fade. Translate and Opacity are independent animations, so this stays smooth on the
    /// composition thread no matter how much the page contains.</summary>
    private static void SlideInPage(UIElement page, TranslateTransform transform)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var slide = new DoubleAnimation
        {
            From = -48,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void RefreshNavIcons()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var normal = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];

        // An approval waiting in ANY agent while you're on Settings/MCP: the agents icon goes gold,
        // because from here you can't see which tab is badged.
        var approvalPending = _currentPage != "chat" && _tabs.Any(t => t.View.IsApprovalOpen);

        NavChatIcon.Foreground = _currentPage == "chat" ? accent : (approvalPending ? gold : normal);
        NavSettingsIcon.Foreground = _currentPage == "settings" ? accent : normal;
        NavMcpIcon.Foreground = _currentPage == "mcp" ? accent : normal;
        NavAppearanceIcon.Foreground = _currentPage == "appearance" ? accent : normal;
        NavSnapshotsIcon.Foreground = _snapshotsPanelOpen ? accent : normal;
        ToolTipService.SetToolTip(NavChat, approvalPending ? "Agents — approval waiting" : "Agents");
    }

    // ============================================================
    // Snapshots panel — global (the store is app-wide), toggled from the rail. Docked left at
    // ~37% width so the active chat stays visible; Import arms the selected agent's next message.
    // ============================================================

    private void NavSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshotsPanelOpen) CloseSnapshots();
        else OpenSnapshots();
    }

    private void CloseSnapshots_Click(object sender, RoutedEventArgs e) => CloseSnapshots();

    private void OpenSnapshots()
    {
        _snapshotsPanelOpen = true;
        SnapshotsPanel.Visibility = Visibility.Visible;
        PopulateSnapshots();
        RefreshNavIcons();
        // Target ~37% of the content area (everything right of the 48px rail), matching the old
        // 0.6* / 1* split. Computed in pixels at open time so the tween can drive the column.
        double target = Math.Max(320, (Root.ActualWidth - 48) * 0.375);
        AnimateSnapshotsColumn(target, hideOnDone: false);
    }

    private void CloseSnapshots()
    {
        _snapshotsPanelOpen = false;
        RefreshNavIcons();
        AnimateSnapshotsColumn(0, hideOnDone: true);
    }

    /// <summary>Tweens the snapshots column width to <paramref name="toPx"/> with an ease-out curve,
    /// gliding the panel open or closed. Re-entrant: a click mid-slide retargets from the current
    /// width rather than restarting from the edge.</summary>
    private void AnimateSnapshotsColumn(double toPx, bool hideOnDone)
    {
        // Drop any in-flight tween so rapid toggles can't stack Rendering handlers.
        if (_snapAnimHandler != null) CompositionTarget.Rendering -= _snapAnimHandler;

        _snapAnimFrom = SnapshotsColumn.Width.IsAbsolute ? SnapshotsColumn.Width.Value : 0;
        _snapAnimTo = toPx;
        _snapAnimHideOnDone = hideOnDone;
        _snapAnimClock.Restart();

        _snapAnimHandler = (_, _) =>
        {
            double t = Math.Clamp(_snapAnimClock.Elapsed.TotalMilliseconds / SnapAnimDurationMs, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            double w = _snapAnimFrom + (_snapAnimTo - _snapAnimFrom) * eased;
            SnapshotsColumn.Width = new GridLength(w, GridUnitType.Pixel);

            if (t >= 1)
            {
                CompositionTarget.Rendering -= _snapAnimHandler;
                _snapAnimHandler = null;
                _snapAnimClock.Stop();
                if (_snapAnimHideOnDone) SnapshotsPanel.Visibility = Visibility.Collapsed;
            }
        };
        CompositionTarget.Rendering += _snapAnimHandler;
    }

    private void OnSnapshotsChanged()
    {
        RefreshSnapshotsBadge();
        if (_snapshotsPanelOpen) PopulateSnapshots();
    }

    private void PopulateSnapshots()
    {
        var items = _snapshotStore.Items;   // newest-first copy of the shared store
        SnapshotsList.ItemsSource = items;
        var empty = items.Count == 0;
        SnapshotsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshSnapshotsBadge();
    }

    private void RefreshSnapshotsBadge()
    {
        var n = _snapshotStore.Count;
        NavSnapshotsBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavSnapshotsBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    private void SnapshotImport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContextSnapshot snap) return;
        var target = _selected?.View;
        if (target == null) return;

        target.Session.Controller.ImportContext(snap);   // arms the active agent's next message
        SwitchPage("chat");   // so the "context armed" note is visible in the active tab
    }

    private void SnapshotDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContextSnapshot snap) return;
        _snapshotStore.Remove(snap);
        PopulateSnapshots();
    }

    /// <summary>"Make Default for New Agents" — snapshot the selected agent's settings to disk.</summary>
    private void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        var agent = _sessions.Active;
        if (agent == null) return;

        _controller.SaveAsDefaults();
        SettingsStatus.Text = $"Saved {agent.Title}'s settings as the default for new agents. "
                            + "Agents already open keep their own.";
    }

    /// <summary>Resets the visible tab's settings to the app's factory defaults (this agent, this
    /// session). Reads a fresh <see cref="MandoCodeConfig"/> for the defaults and applies each key
    /// through the same validated path as editing a field. Leaves connection (endpoint/model) and the
    /// Tavily secret untouched — those aren't "tunable knobs" you'd want wiped by a reset.</summary>
    private async void ResetTab_Click(object sender, RoutedEventArgs e)
    {
        var d = new MandoCodeConfig();   // factory defaults (property initializers)
        var s = SettingsTabs.SelectedItem;
        var resets = new List<(string Key, string Value)>();
        string tabName;

        static string Bool(bool b) => b ? "true" : "false";
        static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (s == Tab_Behavior)
        {
            tabName = "Behavior";
            resets.Add(("taskPlanning", Bool(d.EnableTaskPlanning)));
            resets.Add(("diffApprovals", Bool(d.EnableDiffApprovals)));
            resets.Add(("autoContinue", Bool(d.EnableAutoContinuation)));
            resets.Add(("maxContinuations", Num(d.MaxAutoContinuations)));
            resets.Add(("timeout", Num(d.RequestTimeoutMinutes)));
            resets.Add(("modelResponseTimeout", Num(d.ModelResponseTimeoutSeconds)));
            resets.Add(("toolBudget", Num(d.ToolResultCharBudget)));
            resets.Add(("renderTimeout", Num(d.MarkdownRenderTimeoutSeconds)));
        }
        else if (s == Tab_Integrations)
        {
            tabName = "Integrations";
            resets.Add(("webSearch", Bool(d.EnableWebSearch)));
        }
        else
        {
            tabName = "Model";
            resets.Add(("temperature", d.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
            resets.Add(("maxTokens", Num(d.MaxTokens)));
            resets.Add(("contextLength", Num(d.ContextLength)));
            resets.Add(("streaming", d.ResponseStreaming));
        }

        ResetTabButton.IsEnabled = false;
        foreach (var (key, value) in resets)
            await _controller.ApplyConfigKeyAsync(key, value);
        ResetTabButton.IsEnabled = true;

        LoadSettings();   // reflect the restored values (also clears the status line)
        SettingsStatus.Text = $"{tabName} settings reset to factory defaults.";
    }

    private (Border Header, TextBlock Label, Ellipse Badge) BuildTabHeader(string title)
    {
        var label = new TextBlock
        {
            Text = title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 170
        };

        // Gold dot: an approval is waiting in a tab you aren't looking at.
        var badge = new Ellipse
        {
            Width = 7,
            Height = 7,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"]
        };

        // Options "..." menu (rename / snapshot / export / close) replaces a bare close button — so
        // the last remaining tab isn't stuck showing an X it isn't allowed to use.
        var options = new Button
        {
            Padding = new Thickness(3),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 12 }   // More
        };
        ToolTipService.SetToolTip(options, "Tab options");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(options, "Tab options");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(label);
        row.Children.Add(badge);
        row.Children.Add(options);

        var header = new Border
        {
            Child = row,
            Padding = new Thickness(12, 6, 8, 6),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        return (header, label, badge);
    }

    /// <summary>Wired after the entry exists so the menu handlers can close over it.</summary>
    private void WireHeader(ChatTabEntry entry)
    {
        // The options Button consumes the pointer, so opening its menu doesn't also raise Tapped
        // on the header. Selecting first would be harmless anyway.
        entry.Header.Tapped += (_, _) => SelectTab(entry);

        var row = (StackPanel)entry.Header.Child;
        var options = (Button)row.Children[^1];

        var menu = new MenuFlyout();

        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => _ = RenameTabAsync(entry);

        var snapshot = new MenuFlyoutItem { Text = "Take snapshot", Icon = new FontIcon { Glyph = "" } };
        snapshot.Click += (_, _) => entry.View.TakeSnapshotManually();

        var export = new MenuFlyoutItem { Text = "Export transcript…", Icon = new FontIcon { Glyph = "" } };
        export.Click += (_, _) => _ = entry.View.ExportTranscriptAsync();

        var close = new MenuFlyoutItem { Text = "Close tab", Icon = new FontIcon { Glyph = "" } };
        close.Click += (_, _) => CloseTab(entry);

        menu.Items.Add(rename);
        menu.Items.Add(snapshot);
        menu.Items.Add(export);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(close);

        // The last remaining agent can't be closed (Settings and MCP need one to act on), so grey
        // the item rather than leave a dead button. Re-evaluated each time the menu opens.
        menu.Opening += (_, _) => close.IsEnabled = _tabs.Count > 1;

        options.Flyout = menu;
    }

    /// <summary>Renames a tab via a small dialog. The name is display-only (the folder stays in
    /// the header); it survives folder changes and model switches.</summary>
    private async Task RenameTabAsync(ChatTabEntry entry)
    {
        var box = new TextBox { Text = entry.View.Session.Title };
        box.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename agent",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;

        entry.View.Session.Title = name;
        entry.Label.Text = name;
        RefreshTabStrip();
    }

    /// <summary>Selecting an agent also returns you to the chat page — the Settings you were
    /// looking at belonged to the agent you just left.</summary>
    private void SelectTab(ChatTabEntry entry)
    {
        _selected = entry;
        _sessions.Activate(entry.View.Session);
        RefreshTabStrip();
        SwitchPage("chat");
    }

    private void CloseTab(ChatTabEntry entry)
    {
        var index = _tabs.IndexOf(entry);
        if (index < 0) return;

        // The last agent stays: Settings and MCP have no agent to act on without one.
        if (_tabs.Count == 1) return;

        _tabs.RemoveAt(index);
        TabStrip.Children.Remove(entry.Header);

        // Shut down BEFORE unparenting. Removing the view from the tree unloads the WebView2 and
        // nulls its CoreWebView2, so Close() and any last transcript write would hit null.
        entry.View.Shutdown();
        TabHost.Children.Remove(entry.View);
        _sessions.CloseSession(entry.View.Session);

        if (!ReferenceEquals(_selected, entry))
        {
            RefreshTabStrip();
            return;
        }

        _selected = null;
        SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    private void RefreshTabStrip()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var border = (SolidColorBrush)Application.Current.Resources["MandoBorderBrush"];
        var dim = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var background = (SolidColorBrush)Application.Current.Resources["MandoBackgroundBrush"];
        var transparent = new SolidColorBrush(Colors.Transparent);

        ChatTabEntry? pending = null;

        foreach (var tab in _tabs)
        {
            var isSelected = ReferenceEquals(tab, _selected);
            tab.Header.Background = isSelected ? background : transparent;
            tab.Header.BorderBrush = isSelected ? accent : border;
            tab.Label.Foreground = isSelected ? accent : dim;

            tab.View.IsSelected = isSelected;
            var badged = tab.View.IsApprovalOpen && !isSelected;
            tab.Badge.Visibility = badged ? Visibility.Visible : Visibility.Collapsed;

            // Toast for any approval you can't currently see: a background tab, OR the selected tab
            // while you're away on Settings/MCP/Appearance (its chat — and the approval — is
            // collapsed there, so without this you'd get no notice at all).
            if (tab.View.IsApprovalOpen && (!isSelected || _currentPage != "chat"))
                pending ??= tab;
        }

        // With several agents running, "an approval is waiting" is useless without saying where,
        // so the toast names the agent and selecting it is one click.
        _pendingApprovalTab = pending;
        if (pending != null && !_approvalToastDismissed)
        {
            ApprovalToastText.Text = pending.View.ApprovalHeadline;
            ApprovalToastTarget.Text = $"Click to review in \"{pending.View.Session.Title}\"";
            ApprovalToast.Visibility = Visibility.Visible;
        }
        else
        {
            ApprovalToast.Visibility = Visibility.Collapsed;
            if (pending == null) _approvalToastDismissed = false;   // next approval earns a fresh toast
        }

        RefreshNavIcons();
    }

    private void ApprovalToast_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_pendingApprovalTab != null) SelectTab(_pendingApprovalTab);
    }

    private void ApprovalToastDismiss_Click(object sender, RoutedEventArgs e)
    {
        _approvalToastDismissed = true;
        ApprovalToast.Visibility = Visibility.Collapsed;
    }

    // ============================================================
    // Settings page
    // ============================================================

    private bool _loadingSettings;

    /// <summary>Populates every control from the live config. Guarded so control-change
    /// events fired during population don't write back.</summary>
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            // The SELECTED agent's config, not the saved defaults. Switch agents and this page
            // shows different values.
            var cfg = _controller.Config;
            SettingsAgentChip.Text = _sessions.Active?.Title ?? "";
            EndpointBox.Text = cfg.OllamaEndpoint;
            _modelComboTarget = cfg.GetEffectiveModelName();
            ApplyModelComboTarget();
            S_ContextLength.Value = cfg.ContextLength;
            S_Temperature.Value = cfg.Temperature;
            S_TemperatureLabel.Text = cfg.Temperature.ToString("0.##");
            S_MaxTokens.Value = cfg.MaxTokens;
            S_Streaming.SelectedItem = cfg.ResponseStreaming;
            S_TaskPlanning.IsOn = cfg.EnableTaskPlanning;
            S_DiffApprovals.IsOn = cfg.EnableDiffApprovals;
            S_AutoContinue.IsOn = cfg.EnableAutoContinuation;
            S_MaxContinuations.Value = cfg.MaxAutoContinuations;
            S_RequestTimeout.Value = cfg.RequestTimeoutMinutes;
            S_StallTimeout.Value = cfg.ModelResponseTimeoutSeconds;
            S_ToolBudget.Value = cfg.ToolResultCharBudget;
            S_RenderTimeout.Value = cfg.MarkdownRenderTimeoutSeconds;
            S_WebSearch.IsOn = cfg.EnableWebSearch;
            S_TavilyKey.Password = cfg.TavilyApiKey ?? "";
            S_TavilyKey.PasswordRevealMode = PasswordRevealMode.Hidden;
            TavilyViewButton.Content = "View";
            TavilyViewButton.IsEnabled = !string.IsNullOrEmpty(cfg.TavilyApiKey);
            for (int i = 0; i < UiTheme.All.Count; i++)
                if (UiTheme.All[i] == ThemeManager.Current) ThemeList.SelectedIndex = i;
            SettingsStatus.Text = "";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var s = sender.SelectedItem;
        TabPanel_Model.Visibility = s == Tab_Model ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Behavior.Visibility = s == Tab_Behavior ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Integrations.Visibility = s == Tab_Integrations ? Visibility.Visible : Visibility.Collapsed;

        // "Reset" acts on the visible tab, so its label names that tab.
        ResetTabButtonText.Text = s == Tab_Behavior ? "Reset Behavior"
            : s == Tab_Integrations ? "Reset Integrations" : "Reset Model";
        // Every remaining tab is per-agent now (Appearance moved to its own rail page), so
        // "Make Default for New Agents" always applies.
    }

    private void WindowOpacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // The slider's XAML Value fires this during InitializeComponent, before the
        // label (declared after it) exists — nothing to update yet, the constructor
        // applies the persisted opacity right after the tree is built.
        if (S_WindowOpacityLabel is null) return;
        S_WindowOpacityLabel.Text = $"{(int)e.NewValue}%";
        ThemeManager.SetWindowOpacity(e.NewValue / 100.0);
        ApplyWindowOpacity(ThemeManager.WindowOpacity);
    }

    // WinUI has no Window.Opacity — whole-window translucency is a Win32 layered-window
    // attribute on the HWND. At 100% the layered style is removed entirely so the
    // compositor does no extra work for the default solid window.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private void ApplyWindowOpacity(double opacity)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (opacity >= 0.995)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle & ~(nint)WS_EX_LAYERED);
        }
        else
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | (nint)WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * 255), LWA_ALPHA);
        }
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeList.SelectedItem is not ThemeVm vm) return;
        ThemeManager.Apply(vm.Theme, Root);
        SettingsStatus.Text = $"Theme set to {vm.Theme.Name}.";
    }

    private string _modelComboTarget = "";

    /// <summary>An editable ComboBox drops programmatic Text while its template isn't
    /// loaded (the Settings page starts collapsed) — so the intended model name is kept
    /// here and re-applied on the combo's Loaded event. Selecting the matching pulled
    /// model when one exists also marks it in the dropdown.</summary>
    private void ApplyModelComboTarget()
    {
        if (_modelComboTarget.Length == 0) return;
        if (ModelCombo.ItemsSource is IList<string> models)
        {
            var idx = models.IndexOf(_modelComboTarget);
            if (idx >= 0)
            {
                ModelCombo.SelectedIndex = idx;
                return;
            }
        }
        ModelCombo.Text = _modelComboTarget;
    }

    /// <summary>One write path for the whole page: ConfigKeySetter via the controller.</summary>
    private async Task ApplySettingAsync(string key, string value)
    {
        var (ok, message) = await _controller.ApplyConfigKeyAsync(key, value);
        SettingsStatus.Text = message;
        if (!ok) LoadSettings();   // revert the control to the real value
    }

    private async void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var toggle = (ToggleSwitch)sender;
        await ApplySettingAsync((string)toggle.Tag, toggle.IsOn ? "true" : "false");
    }

    private async void Setting_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings) return;

        // Clearing the box (its "X") or typing something invalid yields NaN. Don't apply it, and
        // don't leave the field empty/stuck — snap back to the last valid value so the spin buttons
        // keep working. If even the old value is gone, reload the whole form from config.
        if (double.IsNaN(args.NewValue))
        {
            if (!double.IsNaN(args.OldValue)) sender.Value = args.OldValue;
            else LoadSettings();
            return;
        }

        await ApplySettingAsync((string)sender.Tag, ((long)args.NewValue).ToString());
    }

    private async void Temperature_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        S_TemperatureLabel.Text = e.NewValue.ToString("0.##");
        await ApplySettingAsync("temperature", e.NewValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async void Streaming_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || S_Streaming.SelectedItem is not string mode) return;
        await ApplySettingAsync("streaming", mode);
    }

    /// <summary>Enables View as soon as there's anything to reveal (saved key or fresh typing).</summary>
    private void TavilyKey_Changed(object sender, RoutedEventArgs e) =>
        TavilyViewButton.IsEnabled = S_TavilyKey.Password.Length > 0;

    private void TavilyView_Click(object sender, RoutedEventArgs e)
    {
        var show = S_TavilyKey.PasswordRevealMode != PasswordRevealMode.Visible;
        S_TavilyKey.PasswordRevealMode = show ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        TavilyViewButton.Content = show ? "Hide" : "View";
    }

    private async void TavilySave_Click(object sender, RoutedEventArgs e)
    {
        var key = S_TavilyKey.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SettingsStatus.Text = "Enter a key first (or type 'clear' to remove the saved one).";
            return;
        }
        await ApplySettingAsync("tavilyKey", key.Trim());
        LoadSettings();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) =>
        await RefreshModelListAsync();

    private async Task RefreshModelListAsync()
    {
        ModelListStatus.Text = "Fetching models…";
        var models = await Task.Run(_controller.ListModelsAsync);
        if (!string.IsNullOrEmpty(ModelCombo.Text)) _modelComboTarget = ModelCombo.Text;
        ModelCombo.ItemsSource = models;
        ApplyModelComboTarget();
        ModelListStatus.Text = models.Count == 0
            ? "No models found — is Ollama running? (ollama serve, then ollama pull <model>)"
            : $"{models.Count} model(s) available.";
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text;
        var model = ModelCombo.Text;
        SettingsStatus.Text = "Connecting… (details land in the chat transcript)";
        await Task.Run(() => _controller.ApplyConnectionSettingsAsync(endpoint, model));
        SettingsStatus.Text = _controller.IsConnected
            ? $"✓ Connected — {_controller.ModelName}"
            : "Couldn't connect — see the chat transcript for details.";
        LoadSettings();
    }

    // ============================================================
    // MCP page
    // ============================================================

    private async void McpRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMcpListAsync();

    private async Task RefreshMcpListAsync()
    {
        // Reflect the active agent's per-agent MCP opt-in (the servers themselves are app-wide).
        // Guard so setting IsOn programmatically doesn't fire Setting_Toggled and re-apply it.
        _loadingSettings = true;
        McpEnableToggle.IsOn = _controller.Config.EnableMcp;
        _loadingSettings = false;

        McpPageStatus.Text = "Checking server status…";
        var rows = await Task.Run(_controller.GetMcpStatusRowsAsync);

        var green = (SolidColorBrush)Application.Current.Resources["MandoGreenBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
        McpList.ItemsSource = rows.Select(r => new McpRow
        {
            Name = r.Name,
            Transport = r.Transport,
            Status = r.Status,
            StatusBrush = r.Connected ? green : gold
        }).ToList();

        McpPageStatus.Text = rows.Count == 0
            ? "No MCP servers configured yet — add one below."
            : $"{rows.Count} server(s) configured.";
    }

    /// <summary>Runs a slash command through the normal pipeline (transcript echo, wizard
    /// overlays, busy state all included), then refreshes the server list.</summary>
    private async Task RunMcpCommandAsync(string command)
    {
        if (_controller.IsProcessing)
        {
            McpPageStatus.Text = "Busy — wait for the current request to finish.";
            return;
        }
        await Task.Run(() => _controller.SubmitAsync(command));
        await RefreshMcpListAsync();
    }

    private void McpAdd_Click(object sender, RoutedEventArgs e) => OpenMcpEditor(null);

    private void McpEdit_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to edit first.";
            return;
        }
        OpenMcpEditor(row.Name);
    }

    private void McpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = McpList.SelectedItem is McpRow;
        McpEditButton.IsEnabled = hasSelection;
        McpRemoveButton.IsEnabled = hasSelection;
    }

    private void McpList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (McpList.SelectedItem is McpRow row) OpenMcpEditor(row.Name);
    }

    private async void McpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to remove first.";
            return;
        }
        await RunMcpCommandAsync($"/mcp remove {row.Name}");
    }

    private async void McpReload_Click(object sender, RoutedEventArgs e) =>
        await RunMcpCommandAsync("/mcp-reload");

    // ============================================================
    // MCP server editor modal (add + edit)
    // ============================================================

    private string? _mcpEditOriginalName;

    private void OpenMcpEditor(string? serverName)
    {
        _mcpEditOriginalName = serverName;
        M_StatusBar.IsOpen = false;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Collapsed;
        McpEditorTestButton.IsEnabled = true;
        McpEditorSaveButton.IsEnabled = true;

        if (serverName != null && _controller.Config.McpServers.TryGetValue(serverName, out var cfg))
        {
            McpEditorTitle.Text = $"Edit MCP server — {serverName}";
            McpEditorSaveButton.Content = "Save & Reconnect";
            M_Name.Text = serverName;
            M_Transport.SelectedIndex = cfg.IsHttp ? 1 : 0;
            M_Command.Text = cfg.Command ?? "";
            M_Args.Text = string.Join(" ", cfg.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            M_Env.Text = string.Join("\n", cfg.Env.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Url.Text = cfg.Url ?? "";
            M_Headers.Text = string.Join("\n", cfg.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Disabled.IsOn = cfg.Disabled;
        }
        else
        {
            McpEditorTitle.Text = "Add MCP server";
            McpEditorSaveButton.Content = "Save & Connect";
            M_Name.Text = "";
            M_Transport.SelectedIndex = 0;
            M_Command.Text = "";
            M_Args.Text = "";
            M_Env.Text = "";
            M_Url.Text = "";
            M_Headers.Text = "";
            M_Disabled.IsOn = false;
        }

        UpdateMcpTransportPanels();
        McpEditorOverlay.Visibility = Visibility.Visible;
        M_Name.Focus(FocusState.Programmatic);
    }

    private void M_Transport_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMcpTransportPanels();

    private void UpdateMcpTransportPanels()
    {
        // Guard: fires during InitializeComponent before panels exist.
        if (M_StdioPanel == null || M_HttpPanel == null) return;
        var isHttp = M_Transport.SelectedIndex == 1;
        M_HttpPanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        M_StdioPanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void McpEditorCancel_Click(object sender, RoutedEventArgs e) =>
        McpEditorOverlay.Visibility = Visibility.Collapsed;

    private void ShowMcpEditorError(string message)
    {
        M_TestSpin.IsActive = false;
        M_TestSpin.Visibility = Visibility.Collapsed;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_StatusBar.Severity = InfoBarSeverity.Error;
        M_StatusBar.Title = "Check the form";
        M_StatusBar.Message = message;
        M_StatusBar.IsOpen = true;
    }

    /// <summary>Parses "KEY=value" lines. Returns null (with an error shown) on a bad line.</summary>
    private Dictionary<string, string>? ParseKeyValueLines(string text, string label)
    {
        var dict = new Dictionary<string, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                ShowMcpEditorError($"{label}: '{line}' isn't KEY=value.");
                return null;
            }
            dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    /// <summary>Shared validate-and-build for Test and Save. Shows the error inline and
    /// returns false when the form isn't valid.</summary>
    private bool TryBuildServerFromForm(bool checkNameCollision, out string name, out MandoCode.Models.McpServerConfig server)
    {
        M_StatusBar.IsOpen = false;
        server = new MandoCode.Models.McpServerConfig { Disabled = M_Disabled.IsOn };

        // Lowercased — servers are referenced by name in tool prefixes (mcp_<server>).
        name = M_Name.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) { ShowMcpEditorError("Name cannot be empty."); return false; }
        if (name.Contains(' ')) { ShowMcpEditorError("Name cannot contain spaces."); return false; }
        if (checkNameCollision && _mcpEditOriginalName == null && _controller.Config.McpServers.ContainsKey(name))
        {
            ShowMcpEditorError($"A server named '{name}' already exists — edit it instead, or pick another name.");
            return false;
        }

        if (M_Transport.SelectedIndex == 1)   // http
        {
            var url = M_Url.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                ShowMcpEditorError("URL must be absolute (e.g. https://mcp.example.com/mcp).");
                return false;
            }
            server.Url = url;
            server.Transport = "http";

            var headers = ParseKeyValueLines(M_Headers.Text, "Headers");
            if (headers == null) return false;
            server.Headers = headers;
        }
        else                                   // stdio
        {
            var command = M_Command.Text.Trim();
            if (string.IsNullOrWhiteSpace(command)) { ShowMcpEditorError("Command cannot be empty."); return false; }
            server.Command = command;
            server.Args = ChatController.ParseShellLikeArgs(M_Args.Text.Trim());

            var env = ParseKeyValueLines(M_Env.Text, "Environment variables");
            if (env == null) return false;
            server.Env = env;
        }

        return true;
    }

    private async void McpEditorTest_Click(object sender, RoutedEventArgs e)
    {
        // No collision check — testing an existing name is fine, nothing is written.
        if (!TryBuildServerFromForm(checkNameCollision: false, out var name, out var server)) return;

        M_StatusBar.Severity = InfoBarSeverity.Informational;
        M_StatusBar.Title = "Testing connection…";
        M_StatusBar.Message = "Connecting with these values — nothing is saved, running servers aren't touched.";
        M_StatusBar.IsOpen = true;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Visible;
        M_TestSpin.IsActive = true;
        McpEditorTestButton.IsEnabled = false;
        McpEditorSaveButton.IsEnabled = false;

        try
        {
            var result = await Task.Run(() => _controller.TestMcpServerAsync(name, server));

            M_TestSpin.IsActive = false;
            M_TestSpin.Visibility = Visibility.Collapsed;

            if (result.Ok)
            {
                M_StatusBar.Severity = InfoBarSeverity.Success;
                M_StatusBar.Title = $"Connected — {result.Tools.Count} tool(s)";
                M_StatusBar.Message = result.Message;
                if (result.Tools.Count > 0)
                {
                    M_TestTools.ItemsSource = result.Tools
                        .Select(t => new ToolChip { Name = t.Name, Description = t.Description ?? "(no description)" })
                        .ToList();
                    M_TestToolsTable.Visibility = Visibility.Visible;
                }
            }
            else
            {
                M_StatusBar.Severity = InfoBarSeverity.Error;
                M_StatusBar.Title = "Connection failed";
                M_StatusBar.Message = result.Message;
            }
        }
        finally
        {
            M_TestSpin.IsActive = false;
            McpEditorTestButton.IsEnabled = true;
            McpEditorSaveButton.IsEnabled = true;
        }
    }

    private async void McpEditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildServerFromForm(checkNameCollision: true, out var name, out var server)) return;

        McpEditorOverlay.Visibility = Visibility.Collapsed;
        SwitchPage("mcp");
        McpPageStatus.Text = $"Saving '{name}' and connecting…";

        var originalName = _mcpEditOriginalName;
        var (_, message) = await Task.Run(() => _controller.SaveMcpServerAsync(originalName, name, server));
        McpPageStatus.Text = message;
        await RefreshMcpListAsync();
        McpPageStatus.Text = message;
    }

}
