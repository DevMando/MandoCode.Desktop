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

public sealed partial class MainWindow
{
    // ============================================================
    // Integrated terminal (VS-style sliding shell panel)
    // ============================================================

    private bool _terminalOpen;
    private bool _terminalMaximized;
    private double _savedTerminalHeight;   // px — the user's last dragged size, restored on reopen
    private double _preMaxHeight;           // px — height to restore to when un-maximizing
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _termAnim;
    private Controls.TerminalPanel? _terminal;   // created lazily on first open

    // Maximized terminal leaves ~5% at the top (just the agent tab strip peeking through).
    private double MaxTerminalHeight() => Math.Max(160, ContentColumnGrid.ActualHeight * 0.95);

    /// <summary>
    /// Builds the terminal panel the first time it's needed and drops it into Grid.Row 3 of the
    /// content column. Deferred so no WebView2 or shell process is created until the user opens a
    /// terminal.
    /// </summary>
    private Controls.TerminalPanel EnsureTerminal()
    {
        if (_terminal != null) return _terminal;

        _terminal = new Controls.TerminalPanel { Visibility = Visibility.Collapsed };
        Grid.SetRow(_terminal, 3);
        ContentColumnGrid.Children.Add(_terminal);

        // Each new shell opens in whichever agent tab is active at the time.
        _terminal.WorkingDirectoryProvider = () => ActiveChat?.Session.ProjectRoot.ProjectRoot;
        _terminal.CloseRequested += (_, _) => CloseTerminalPanel();
        _terminal.MaximizeRequested += (_, _) => ToggleMaximizeTerminal();

        // Agent output tabs can only be built once the xterm host is live. Until then every
        // agent's AgentCommandLog is holding its own scrollback, so nothing is lost by waiting.
        _terminal.Ready += (_, _) => AttachAllAgentOutputs();
        _terminal.AgentOutputClosed += (_, key) => FindSessionByKey(key)?.CommandLog.Clear();
        if (_terminal.IsReady) AttachAllAgentOutputs();
        return _terminal;
    }

    // ============================================================
    // Agent command output (read-only terminal tabs)
    // ============================================================

    /// <summary>Agents already piped into the terminal panel, so a second call can't double-subscribe
    /// and echo every line twice.</summary>
    private readonly HashSet<string> _agentOutputAttached = new();

    /// <summary>
    /// The panel is shared by every agent, so each one's output needs a stable key to own a tab
    /// with. PersistKey is that key: it already survives restarts and renames.
    /// </summary>
    private static string AgentOutputKey(AgentSession session) => session.PersistKey;

    private AgentSession? FindSessionByKey(string key) =>
        _tabs.FirstOrDefault(t => AgentOutputKey(t.View.Session) == key)?.View.Session;

    private void AttachAllAgentOutputs()
    {
        foreach (var tab in _tabs) AttachAgentOutput(tab.View.Session);
    }

    /// <summary>
    /// Pipes one agent's command log into the terminal panel: replays what it has already recorded,
    /// then follows it live. Safe to call repeatedly — only the first call for an agent subscribes.
    /// No-op until the panel exists and its xterm host is up; <see cref="AttachAllAgentOutputs"/>
    /// runs then and catches up whatever was missed.
    /// </summary>
    private void AttachAgentOutput(AgentSession session)
    {
        var panel = _terminal;
        if (panel == null || !panel.IsReady) return;

        var key = AgentOutputKey(session);
        if (!_agentOutputAttached.Add(key)) return;

        var title = session.Title;
        var backlog = session.CommandLog.Snapshot();
        if (!string.IsNullOrEmpty(backlog)) panel.WriteAgentOutput(key, title, backlog);

        // Raised on the command's own reader threads — hop to the UI thread before touching the
        // WebView. The log keeps its copy either way, so a dropped enqueue costs a live update,
        // never the scrollback.
        session.CommandLog.Appended += text =>
            DispatcherQueue.TryEnqueue(() => _terminal?.WriteAgentOutput(key, session.Title, text));
    }

    private void NavTerminal_Click(object sender, RoutedEventArgs e) => ToggleTerminal();

    private void ToggleTerminal()
    {
        if (_terminalOpen) CloseTerminalPanel();
        else OpenTerminalPanel();
    }

    private void OpenTerminalPanel()
    {
        var term = EnsureTerminal();
        if (_terminalOpen) { term.FocusActive(); return; }
        _terminalOpen = true;
        RefreshNavIcons();

        term.Visibility = Visibility.Visible;
        TerminalSplitter.Visibility = Visibility.Visible;
        term.EnsureStartedAsync();

        double target = _savedTerminalHeight > 0 ? _savedTerminalHeight : DefaultTerminalHeight();
        AnimateTerminalHeight(target, onDone: () => term.Refit());
    }

    private void CloseTerminalPanel()
    {
        if (!_terminalOpen) return;
        _terminalOpen = false;
        RefreshNavIcons();

        double current = TerminalRow.Height.Value;
        if (current > 40) _savedTerminalHeight = current;   // remember size for next time

        AnimateTerminalHeight(0, onDone: () =>
        {
            if (_terminal != null) _terminal.Visibility = Visibility.Collapsed;
            TerminalSplitter.Visibility = Visibility.Collapsed;
            ActiveChat?.FocusInput();
        });
    }

    /// <summary>Expand the terminal to ~95% of the window (5% left at top), or restore its prior size.</summary>
    private void ToggleMaximizeTerminal()
    {
        if (!_terminalOpen) { OpenTerminalPanel(); return; }   // first open lands at the default size

        if (_terminalMaximized)
        {
            _terminalMaximized = false;
            double restore = _preMaxHeight > 40 ? _preMaxHeight : DefaultTerminalHeight();
            AnimateTerminalHeight(restore, onDone: () => _terminal?.Refit());
        }
        else
        {
            _terminalMaximized = true;
            _preMaxHeight = TerminalRow.Height.Value;
            AnimateTerminalHeight(MaxTerminalHeight(), onDone: () => _terminal?.Refit());
        }
        _terminal?.SetMaximized(_terminalMaximized);
    }

    private double DefaultTerminalHeight()
    {
        double h = ContentColumnGrid.ActualHeight;
        if (h <= 0) h = 800;
        return Math.Clamp(h * 0.30, 140, h * 0.7);
    }

    /// <summary>
    /// Slides the terminal row to <paramref name="target"/> px over ~160ms. A short,
    /// self-terminating step timer (never an indefinite animation — see the WebView
    /// repaint history in project memory).
    /// </summary>
    private void AnimateTerminalHeight(double target, Action? onDone)
    {
        _termAnim?.Stop();

        double start = TerminalRow.Height.Value;
        if (Math.Abs(target - start) < 0.5)
        {
            TerminalRow.Height = new GridLength(target);
            onDone?.Invoke();
            return;
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        var sw = Stopwatch.StartNew();
        const double durationMs = 160;

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            TerminalRow.Height = new GridLength(Math.Max(0, start + (target - start) * eased));
            if (t >= 1.0)
            {
                timer.Stop();
                TerminalRow.Height = new GridLength(target);
                onDone?.Invoke();
            }
        };
        _termAnim = timer;
        timer.Start();
    }

    private bool _draggingSplitter;
    private double _dragStartHeight;
    private double _dragStartY;

    private void TerminalSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = true;
        _dragStartHeight = TerminalRow.Height.Value;
        _dragStartY = e.GetCurrentPoint(Root).Position.Y;   // Root frame — stable as the grip moves
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TerminalSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        // Dragging up grows the terminal; down shrinks it. Ceiling is the maximized height (~95%).
        double delta = e.GetCurrentPoint(Root).Position.Y - _dragStartY;
        double next = Math.Clamp(_dragStartHeight - delta, 80, MaxTerminalHeight());
        TerminalRow.Height = new GridLength(next);
    }

    private void TerminalSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        _draggingSplitter = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);

        _savedTerminalHeight = TerminalRow.Height.Value;
        // Keep the maximize button's glyph honest: dragging near the top counts as maximized.
        _terminalMaximized = TerminalRow.Height.Value >= ContentColumnGrid.ActualHeight * 0.9;
        if (!_terminalMaximized) _preMaxHeight = TerminalRow.Height.Value;
        _terminal?.SetMaximized(_terminalMaximized);
        _terminal?.Refit();
    }

    private bool _initialized;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        // Restored workspaces can open with several tabs — initialize them all (each owns
        // its WebView2 + harness, same cost as if the user had opened them by hand).
        // Every tab boots on the DEFAULT model and only moves to its own saved model once
        // InitTabAsync gets that far. Workspace writes are suppressed until the last tab settles
        // — see SaveWorkspace for why a write inside that window loses a tab's model.
        Volatile.Write(ref _restoringTabs, _tabs.Count);
        foreach (var entry in _tabs) _ = InitTabAsync(entry);
        InitBgPreview();

        // Both stores load from disk at construction; reflect their counts on the rail at launch,
        // before the user opens either panel.
        RefreshSnapshotsBadge();
        RefreshHistoryBadge();
    }

    /// <summary>Tabs still moving from the default model onto their own saved one.</summary>
    private int _restoringTabs;

    private async Task InitTabAsync(ChatTabEntry entry)
    {
        try
        {
            await RestoreTabAsync(entry);
            // Restore ran to completion, so whatever model this tab holds now is the one worth
            // remembering — including a model that errored, since the name still resolved. A throw
            // skips this line, leaving the saved model protected by SaveWorkspace instead.
            entry.ModelRestorePending = false;
        }
        finally
        {
            // Settings persistence opens here for the same reason workspace writes do: until the
            // restore cascade has run, this tab is still on the seeded default, and a write in that
            // window would save the default over the user's own settings. A throw above still arms
            // it — the tab is done moving either way, and it must not be stuck read-only for the
            // rest of the session.
            entry.View.Session.ConfigPersistenceArmed = true;
            // Last one out writes the workspace, now that every tab reports its real model.
            if (Interlocked.Decrement(ref _restoringTabs) == 0) SaveWorkspace();
        }
    }

    private async Task RestoreTabAsync(ChatTabEntry entry)
    {
        var controller = entry.View.Session.Controller;
        // Best-effort per-tab model restore: if the saved model is gone (Ollama not running,
        // cloud model renamed), the tab simply keeps the default and says so in its header.
        var desired = entry.RestoreModel;
        // Boot would announce the config default before the saved model replaces it. Hold that
        // announcement until the model has settled, then let exactly one branch make it.
        controller.DeferModelAnnouncement = !string.IsNullOrEmpty(desired);
        await entry.View.InitializeAsync();
        if (!string.IsNullOrEmpty(desired) && desired != controller.ModelName)
        {
            await Task.Run(() => controller.SelectModelAsync(desired));
            entry.View.UpdateHeader();
        }
        else if (controller.DeferModelAnnouncement)
        {
            // The saved model was already the active one, so no switch fired to announce it.
            controller.AnnounceModelStatus();
        }
        controller.DeferModelAnnouncement = false;

        // Memory comes back only after the model has settled — selecting a model clears
        // history, so this order is what keeps the restored memory alive.
        await entry.View.RestoreConversationMemoryAsync();
    }

    /// <summary>Writes the current workspace shape (tabs + active) to disk. Called on close
    /// and after any structural change, so even a crash loses at most the latest tweak.</summary>
    /// <summary>
    /// The model to remember for a tab: its live model, except while a restore has not finished.
    /// A tab mid-restore (or one whose restore threw) is still on the default it was seeded with,
    /// and persisting that would replace the user's own choice with the default for good.
    /// </summary>
    private static string PersistedModelFor(ChatTabEntry tab) =>
        tab.ModelRestorePending && !string.IsNullOrWhiteSpace(tab.RestoreModel)
            ? tab.RestoreModel
            : tab.View.Session.Controller.ModelName;

    private void SaveWorkspace()
    {
        // This records the CURRENT model of EVERY tab, and a restore reaches it early: any tab's
        // StateChanged during startup runs UpdateHeader -> HeaderChanged -> here, while other tabs
        // are still sitting on the default. Writing then stamps the default over a tab's real
        // model, and that tab comes back on the default next launch — the switch is simply lost.
        if (Volatile.Read(ref _restoringTabs) > 0) return;

        var tabs = _tabs.Select(t => new WorkspaceTabState(
            t.View.Session.Title,
            t.View.Session.ProjectRoot.ProjectRoot,
            PersistedModelFor(t),
            t.View.Session.PersistKey)).ToList();
        var active = _selected == null ? 0 : Math.Max(0, _tabs.IndexOf(_selected));

        // The split layout rides along: paned agents (by persist-key) plus the divider positions.
        // Null when no split is configured, which restores as plain single view.
        var panes = SplitConfigured
            ? _splitPanes.Select(p => p.View.Session.PersistKey).ToList()
            : null;
        WorkspaceState.Save(new WorkspaceShape(
            tabs, active, panes,
            panes == null ? null : new List<double>(_colFractions),
            panes == null ? null : new List<double>(_rowFractions)));

        // Safety net for per-agent settings. ChatController.ConfigChanged catches the deliberate
        // edits; this catches anything that mutates a config without raising it (a healed endpoint,
        // a wizard). Each call is a fingerprint compare and writes nothing when nothing moved.
        foreach (var tab in _tabs) tab.View.Session.PersistConfigIfChanged();
    }

}
