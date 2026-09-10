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

        // Both null for the agent-output tab: it has no shell and no process behind it. That is
        // what makes it read-only — there is nothing for a keystroke to be written to.
        public ShellSpec? Shell { get; init; }
        public TerminalSession? Session { get; init; }
        public required Border Header { get; init; }
        public required TextBlock Title { get; init; }

        /// <summary>The agent this tab mirrors, for an output tab; null for a real shell.</summary>
        public string? AgentKey { get; init; }

        /// <summary>New output arrived while this tab was not the visible one.</summary>
        public bool Unread;

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

    /// <summary>
    /// Raised once the xterm host is live and tabs can be created. The host waits for this before
    /// replaying agent scrollback, so nothing has to be buffered twice — <see cref="AgentCommandLog"/>
    /// already holds it until someone can display it.
    /// </summary>
    public event EventHandler? Ready;

    /// <summary>Raised with an agent key when the user closes that agent's output tab.</summary>
    public event EventHandler<string>? AgentOutputClosed;

    /// <summary>True once the xterm host has reported in and <see cref="WriteAgentOutput"/> works.</summary>
    public bool IsReady => _webReady;

    /// <summary>Raised when the visible tab changes, so the host can drop an unread cue.</summary>
    public event EventHandler? ActiveTabChanged;

    /// <summary>
    /// True when the visible tab is an agent's output rather than a shell. The distinction matters
    /// for the rail's unread badge: having the panel open on a shell tab is not the same as having
    /// read what an agent printed.
    /// </summary>
    public bool ActiveTabIsAgentOutput =>
        _activeId != null && _tabs.TryGetValue(_activeId, out var active) && active.AgentKey != null;

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

        // Every new shell session gets a look — MandoCliDetector's own counter decides
        // whether THIS is the one-in-N that actually shows anything.
        MaybeShowCliHint(id);
    }

    /// <summary>Periodic, best-effort nudge toward the mandocode CLI — see MandoCliDetector for
    /// the cadence and why every uncertain case stays quiet. Detection runs off the UI thread
    /// (file/PATH checks); the note itself is written straight into the fresh shell's own xterm
    /// buffer, never through its stdin, so it can't be mistaken for a command. The short delay
    /// lets the shell print its own startup banner/prompt first, so this reads as "after the
    /// prompt" instead of racing it.</summary>
    private void MaybeShowCliHint(string id)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!MandoCliDetector.ShouldShowHint()) return;

                await Task.Delay(700);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_tabs.ContainsKey(id))
                        Post(new
                        {
                            type = "note",
                            id,
                            message = "(tip: mandocode CLI not found — install with: dotnet tool install --global MandoCode)"
                        });
                });
            }
            catch { /* never disrupt the terminal over this */ }
        });
    }

    // ---- Agent output tabs (read-only) -----------------------------------------

    /// <summary>
    /// Writes an agent's shell-command output into that agent's own read-only tab, creating the tab
    /// on first use. The text is already terminal-formatted by <see cref="AgentCommandFormat"/>.
    ///
    /// <para>Creating a tab never switches to it. An agent running a build while you are typing in
    /// a shell must not steal the panel out from under you — the tab title accents instead, and you
    /// look when you want to.</para>
    ///
    /// <para>Must be called on the UI thread, and only once <see cref="IsReady"/> is true; output
    /// arriving earlier stays in the agent's <see cref="AgentCommandLog"/> until the host replays
    /// it.</para>
    /// </summary>
    public void WriteAgentOutput(string agentKey, string tabTitle, string text)
    {
        if (!_webReady || string.IsNullOrEmpty(text)) return;

        var tab = EnsureAgentTab(agentKey, tabTitle);
        if (tab == null) return;

        Post(new { type = "write", id = tab.Id, data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) });

        if (_activeId != tab.Id)
        {
            tab.Unread = true;
            ApplyTabStyle(tab, active: false);
        }
    }

    /// <summary>
    /// Brings an agent output tab to the front, if one exists. Called when the panel is opened with
    /// output waiting: the panel always creates a starter shell on first open, so without this the
    /// user follows the rail badge, lands on an empty prompt, and has to hunt for the tab they came
    /// for. Returns false when there is no output tab to show, leaving the shell in front.
    /// </summary>
    public bool FocusAgentOutput()
    {
        var tab = _tabs.Values.FirstOrDefault(t => t.AgentKey != null);
        if (tab == null) return false;
        SwitchTo(tab.Id);
        return true;
    }

    /// <summary>Renames an agent's output tab in place, so a renamed agent stays recognizable.</summary>
    public void RenameAgentOutput(string agentKey, string tabTitle)
    {
        var tab = _tabs.Values.FirstOrDefault(t => t.AgentKey == agentKey);
        if (tab != null) tab.Title.Text = tabTitle;
    }

    private TerminalTab? EnsureAgentTab(string agentKey, string tabTitle)
    {
        var existing = _tabs.Values.FirstOrDefault(t => t.AgentKey == agentKey);
        if (existing != null) return existing;

        string id = "a" + (++_tabCounter);
        var (header, title) = BuildTabHeader(id, shell: null, outputTitle: tabTitle);
        var tab = new TerminalTab { Id = id, Header = header, Title = title, AgentKey = agentKey };
        _tabs[id] = tab;
        TabStrip.Children.Add(header);

        // readOnly tells xterm not to accept or forward input at all; there is no process behind
        // this tab for a keystroke to reach.
        Post(new { type = "create", id, cols = 80, rows = 24, readOnly = true });
        ApplyTabStyle(tab, active: false);
        return tab;
    }

    private (Border header, TextBlock title) BuildTabHeader(string id, ShellSpec? shell, string? outputTitle = null)
    {
        var title = new TextBlock
        {
            Text = outputTitle ?? shell?.DisplayName ?? "Shell",
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
        // A distinct glyph for the read-only output tab, so it is not mistaken for a
        // shell you can type in before the cursor ever gets there.
        content.Children.Add(new FontIcon { Glyph = outputTitle != null ? "\uE9D9" : "\uE756", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
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
        if (!_tabs.TryGetValue(id, out var target)) return;
        _activeId = id;
        target.Unread = false;
        Post(new { type = "show", id });

        foreach (var t in _tabs.Values)
            ApplyTabStyle(t, active: t.Id == id);

        ActiveTabChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTabStyle(TerminalTab tab, bool active)
    {
        tab.Header.Background = active
            ? (Brush)Application.Current.Resources["MandoBackgroundBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        tab.Title.Opacity = active ? 1.0 : 0.65;
        // An agent can produce output while you are looking at a different tab. Accenting the
        // title is the only cue that anything happened — the panel deliberately never switches
        // tabs on its own, which would yank you out of a shell mid-command.
        tab.Title.Foreground = tab.Unread && !active
            ? (Brush)Application.Current.Resources["MandoAccentBrush"]
            : (Brush)Application.Current.Resources["MandoTextBrush"];
    }

    private void CloseTab(string id)
    {
        if (!_tabs.TryGetValue(id, out var tab)) return;

        try { tab.Session?.Dispose(); } catch { }
        Post(new { type = "dispose", id });
        TabStrip.Children.Remove(tab.Header);
        _tabs.Remove(id);

        // Closing an output tab is a dismissal, not a pause: tell the host so it clears that
        // agent's scrollback. Otherwise the next command would reopen the tab and replay
        // everything the user just closed.
        if (tab.AgentKey != null) AgentOutputClosed?.Invoke(this, tab.AgentKey);

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
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;

                case "data":
                    // Null session = the agent-output tab. xterm is told not to send input for it,
                    // so this is belt-and-braces: a keystroke that arrives anyway is dropped rather
                    // than being written into some other tab's shell.
                    if (TryGetTab(root, out var t) && t != null)
                        t.Session?.Write(root.GetProperty("data").GetString() ?? "");
                    break;

                case "resize":
                    if (TryGetTab(root, out var rt) && rt != null)
                    {
                        short cols = (short)root.GetProperty("cols").GetInt32();
                        short rows = (short)root.GetProperty("rows").GetInt32();
                        rt.Session?.Resize(cols, rows);
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
            try { tab.Session?.Dispose(); } catch { }
        }
        _tabs.Clear();
    }
}
