using System.IO;
using System.Text.Json;
using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// The integrated terminal — a VS-style panel that hosts one or more real shells
/// (PowerShell, cmd, Git Bash, …) via ConPTY, rendered with xterm.js inside a single
/// WebView2. Multiple shell tabs are multiplexed in the one WebView; each is backed by
/// its own <see cref="TerminalSession"/>. Lives once, shared across all agent tabs.
/// </summary>
public sealed partial class TerminalPanel : UserControl
{
    private sealed class TerminalTab
    {
        public required string Id { get; init; }
        public required ShellSpec Shell { get; init; }
        public required TerminalSession Session { get; init; }
        public required Border Header { get; init; }
        public required TextBlock Title { get; init; }

        // Output coalescing: the read thread appends here; a single UI-thread flush
        // drains it, so a burst of small reads becomes one write across the WebView bridge.
        public readonly List<byte[]> Pending = new();
        public bool FlushScheduled;
        public bool Exited;
    }

    private readonly Dictionary<string, TerminalTab> _tabs = new();
    private string? _activeId;
    private int _tabCounter;
    private bool _webInitStarted;
    private bool _webReady;

    /// <summary>Supplies the cwd for a new shell (the active agent's project folder).</summary>
    public Func<string?>? WorkingDirectoryProvider { get; set; }

    /// <summary>Raised when the user asks to hide the panel (close button / last tab closed).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the user toggles maximize/restore (host owns the row height).</summary>
    public event EventHandler? MaximizeRequested;

    /// <summary>Updates the maximize button's glyph/tooltip to reflect the current state.</summary>
    public void SetMaximized(bool maximized)
    {
        MaximizeIcon.Glyph = maximized ? "" : "";   // BackToWindow : FullScreen
        ToolTipService.SetToolTip(MaximizeButton, maximized ? "Restore terminal size" : "Maximize terminal");
    }

    public TerminalPanel()
    {
        InitializeComponent();
        BuildShellMenu();
    }

    public bool HasSessions => _tabs.Count > 0;

    // ---- Startup ---------------------------------------------------------------

    /// <summary>
    /// Initializes the WebView (once) and guarantees at least one live shell. Safe to
    /// call every time the panel is shown.
    /// </summary>
    public async void EnsureStartedAsync()
    {
        if (!_webInitStarted)
        {
            _webInitStarted = true;
            await InitializeWebAsync();
        }

        if (_webReady && _tabs.Count == 0)
            AddTab(ShellCatalog.Default());
        else
            FocusActive();
    }

    private async System.Threading.Tasks.Task InitializeWebAsync()
    {
        try
        {
            await TermView.EnsureCoreWebView2Async();
            var core = TermView.CoreWebView2;
            if (core == null) return;

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;

            core.WebMessageReceived += OnWebMessage;

            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "mandocode.assets",
                    Path.Combine(AppContext.BaseDirectory, "Assets", "web"),
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            core.Navigate("https://mandocode.assets/terminal/terminal.html");
        }
        catch { /* WebView2 runtime missing — terminal simply won't open */ }
    }

    // ---- Shell menu ------------------------------------------------------------

    private void BuildShellMenu()
    {
        ShellMenu.Items.Clear();
        foreach (var shell in ShellCatalog.Available())
        {
            var item = new MenuFlyoutItem { Text = shell.DisplayName, Tag = shell };
            item.Click += (_, _) => AddTab(shell);
            ShellMenu.Items.Add(item);
        }
    }

    private void NewTerminal_Click(SplitButton sender, SplitButtonClickEventArgs args) => AddTab(ShellCatalog.Default());

    /// <summary>Open a new default shell tab (used by the Ctrl+Shift+` accelerator).</summary>
    public void NewTerminalTab() => AddTab(ShellCatalog.Default());

    // ---- Tab lifecycle ---------------------------------------------------------

    private void AddTab(ShellSpec shell)
    {
        if (!_webReady) return;   // ignored until the WebView is up; EnsureStarted retries

        string id = "t" + (++_tabCounter);
        string? cwd = WorkingDirectoryProvider?.Invoke();

        TerminalSession session;
        try
        {
            session = new TerminalSession(id, shell, cwd, columns: 80, rows: 24);
        }
        catch (Exception ex)
        {
            ShowError($"Could not start {shell.DisplayName}: {ex.Message}");
            return;
        }

        var (header, title) = BuildTabHeader(id, shell);
        var tab = new TerminalTab { Id = id, Shell = shell, Session = session, Header = header, Title = title };
        _tabs[id] = tab;
        TabStrip.Children.Add(header);

        session.OutputReceived += bytes => OnOutput(tab, bytes);
        session.Exited += () => DispatcherQueue.TryEnqueue(() => OnSessionExited(tab));

        // Spin up the matching xterm instance and switch to it.
        Post(new { type = "create", id, cols = 80, rows = 24 });
        SwitchTo(id);
    }

    private (Border header, TextBlock title) BuildTabHeader(string id, ShellSpec shell)
    {
        var title = new TextBlock
        {
            Text = shell.DisplayName,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var closeButton = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, e) => CloseTab(id);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        content.Children.Add(title);
        content.Children.Add(closeButton);

        var header = new Border
        {
            Child = content,
            Padding = new Thickness(9, 4, 5, 4),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = id,
        };
        header.Tapped += (_, _) => SwitchTo(id);
        return (header, title);
    }

    private void SwitchTo(string id)
    {
        if (!_tabs.ContainsKey(id)) return;
        _activeId = id;
        Post(new { type = "show", id });

        foreach (var t in _tabs.Values)
            ApplyTabStyle(t, active: t.Id == id);
    }

    private void ApplyTabStyle(TerminalTab tab, bool active)
    {
        tab.Header.Background = active
            ? (Brush)Application.Current.Resources["MandoBackgroundBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        tab.Title.Opacity = active ? 1.0 : 0.65;
    }

    private void CloseTab(string id)
    {
        if (!_tabs.TryGetValue(id, out var tab)) return;

        try { tab.Session.Dispose(); } catch { }
        Post(new { type = "dispose", id });
        TabStrip.Children.Remove(tab.Header);
        _tabs.Remove(id);

        if (_activeId == id) _activeId = null;

        if (_tabs.Count == 0)
        {
            // Nothing left to show — fold the panel away; it reopens with a fresh shell.
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (_activeId == null)
        {
            SwitchTo(_tabs.Keys.Last());
        }
    }

    private void OnSessionExited(TerminalTab tab)
    {
        if (tab.Exited) return;
        tab.Exited = true;
        Post(new { type = "exited", id = tab.Id, message = "[process exited — press the × to close this tab]" });
        tab.Title.Opacity = 0.5;
    }

    // ---- Output pump (bg thread -> coalesced UI flush) -------------------------

    private void OnOutput(TerminalTab tab, byte[] bytes)
    {
        bool schedule;
        lock (tab.Pending)
        {
            tab.Pending.Add(bytes);
            schedule = !tab.FlushScheduled;
            if (schedule) tab.FlushScheduled = true;
        }
        if (schedule) DispatcherQueue.TryEnqueue(() => FlushTab(tab));
    }

    private void FlushTab(TerminalTab tab)
    {
        byte[] combined;
        lock (tab.Pending)
        {
            tab.FlushScheduled = false;
            if (tab.Pending.Count == 0) return;
            int total = tab.Pending.Sum(b => b.Length);
            combined = new byte[total];
            int offset = 0;
            foreach (var chunk in tab.Pending)
            {
                Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
                offset += chunk.Length;
            }
            tab.Pending.Clear();
        }
        Post(new { type = "write", id = tab.Id, data = Convert.ToBase64String(combined) });
    }

    // ---- Bridge (JS -> C#) -----------------------------------------------------

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrEmpty(raw)) return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type)
            {
                case "ready":
                    _webReady = true;
                    if (_tabs.Count == 0) AddTab(ShellCatalog.Default());
                    break;

                case "data":
                    if (TryGetTab(root, out var t) && t != null)
                        t.Session.Write(root.GetProperty("data").GetString() ?? "");
                    break;

                case "resize":
                    if (TryGetTab(root, out var rt) && rt != null)
                    {
                        short cols = (short)root.GetProperty("cols").GetInt32();
                        short rows = (short)root.GetProperty("rows").GetInt32();
                        rt.Session.Resize(cols, rows);
                    }
                    break;
            }
        }
        catch { /* malformed message — ignore */ }
    }

    private bool TryGetTab(JsonElement root, out TerminalTab? tab)
    {
        tab = null;
        if (root.TryGetProperty("id", out var idProp) &&
            idProp.GetString() is string id &&
            _tabs.TryGetValue(id, out var found))
        {
            tab = found;
            return true;
        }
        return false;
    }

    // ---- Toolbar buttons -------------------------------------------------------

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_activeId != null) Post(new { type = "write", id = _activeId, data = Convert.ToBase64String(new byte[] { 0x1b, (byte)'c' }) });
    }

    private void HidePanel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Maximize_Click(object sender, RoutedEventArgs e) => MaximizeRequested?.Invoke(this, EventArgs.Empty);

    // ---- Helpers ---------------------------------------------------------------

    /// <summary>Focus the active terminal so keystrokes land immediately after the panel opens.</summary>
    public void FocusActive()
    {
        if (_activeId != null) Post(new { type = "focus", id = _activeId });
    }

    /// <summary>Refit the active terminal after the panel is resized (host row height changed).</summary>
    public void Refit()
    {
        if (_activeId != null) Post(new { type = "fit", id = _activeId });
    }

    private void ShowError(string message)
    {
        // Surface start-up failures in the active terminal if there is one, else via a menu item.
        if (_activeId != null)
            Post(new { type = "exited", id = _activeId, message = message });
    }

    private void Post(object payload)
    {
        var core = TermView.CoreWebView2;
        if (core == null) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
    }

    /// <summary>Kill every shell — call on window close so no ConPTY processes leak.</summary>
    public void ShutDown()
    {
        foreach (var tab in _tabs.Values)
        {
            try { tab.Session.Dispose(); } catch { }
        }
        _tabs.Clear();
    }
}
