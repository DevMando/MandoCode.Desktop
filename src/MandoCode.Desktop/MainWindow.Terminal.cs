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
        return _terminal;
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
        foreach (var entry in _tabs) _ = InitTabAsync(entry);
        InitBgPreview();

        // Both stores load from disk at construction; reflect their counts on the rail at launch,
        // before the user opens either panel.
        RefreshSnapshotsBadge();
        RefreshHistoryBadge();
    }

    private async Task InitTabAsync(ChatTabEntry entry)
    {
        await entry.View.InitializeAsync();
        // Best-effort per-tab model restore: if the saved model is gone (Ollama not running,
        // cloud model renamed), the tab simply keeps the default and says so in its header.
        var desired = entry.RestoreModel;
        if (!string.IsNullOrEmpty(desired) && desired != entry.View.Session.Controller.ModelName)
        {
            await Task.Run(() => entry.View.Session.Controller.SelectModelAsync(desired));
            entry.View.UpdateHeader();
        }

        // Memory comes back only after the model has settled — selecting a model clears
        // history, so this order is what keeps the restored memory alive.
        await entry.View.RestoreConversationMemoryAsync();
    }

    /// <summary>Writes the current workspace shape (tabs + active) to disk. Called on close
    /// and after any structural change, so even a crash loses at most the latest tweak.</summary>
    private void SaveWorkspace()
    {
        var tabs = _tabs.Select(t => new WorkspaceTabState(
            t.View.Session.Title,
            t.View.Session.ProjectRoot.ProjectRoot,
            t.View.Session.Controller.ModelName,
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
    }

}
