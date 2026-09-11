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
    // Split view — 2 to 4 agents at once. The PANE SET (_splitPanes, in pane order)
    // is a remembered, explicit choice: set only by the Split button, the split bar's chips, and the
    // tab menu's "Add to split view" — NEVER by plain-clicking a tab. The split is shown whenever the
    // active tab (_selected) is one of the paned agents; clicking any other tab shows that agent
    // normally while the set waits, and clicking a paned tab brings the split back. Panes are
    // ordinary tab views moved between grid cells by ApplyPaneLayout — never reparented, so their
    // WebViews survive.
    // ============================================================

    // Grid geometry and the divider math live in PaneLayout (pure, unit-tested); this file owns the
    // visual-tree side — building tracks, creating dividers, moving views between cells.
    private const int MaxSplitPanes = PaneLayout.MaxPanes;

    private readonly List<ChatTabEntry> _splitPanes = new();

    /// <summary>
    /// One header per pane, naming the agent shown beneath it. Rebuilt with the tracks, the same way
    /// the dividers are.
    ///
    /// <para>They exist because a strip along the top is a poor map for a spatial layout: with four
    /// panes you have to hold "second chip means bottom-left" in your head, which is exactly the work
    /// the split is meant to save. A label sitting on the thing it names needs no mapping.</para>
    ///
    /// <para>Deliberately a SIBLING of the agent view in the same grid cell rather than a wrapper
    /// around it. Wrapping would reparent the view, and reparenting tears down its WebView2 — the
    /// constraint this whole file is built around. Two children share the cell; the header pins to
    /// the top and the view carries a matching top margin.</para>
    /// </summary>
    private readonly List<Border> _paneHeaders = new();

    /// <summary>Height of a pane header, and therefore the top margin a paned view carries. Kept
    /// tight on purpose — in a 2x2 it is spent twice over, and it is chat area either way — but tall
    /// enough that a 13px name is not squeezed against the edges.</summary>
    private const double PaneHeaderHeight = 30;

    // Divider positions as star fractions — one entry per pane COLUMN and per pane ROW. Reset to
    // equal when the layout shape changes; otherwise preserved across page visits and restarts.
    private List<double> _colFractions = new();
    private List<double> _rowFractions = new();

    // Dividers are rebuilt with the tracks; held so the next rebuild can remove the old ones.
    private readonly List<Controls.ResizeGrip> _paneGrips = new();

    private bool _syncingSplitBar;
    private bool _draggingPaneGrip;

    /// <summary>A valid pane set is configured: 2–4 distinct agents, all still open.</summary>
    private bool SplitConfigured =>
        _splitPanes.Count >= 2
        && _splitPanes.Count <= MaxSplitPanes
        && _splitPanes.All(p => _tabs.Contains(p))
        && _splitPanes.Distinct().Count() == _splitPanes.Count;

    /// <summary>The split is actually being shown right now: a set exists, we're on the chat page,
    /// and the active tab is one of the paned agents (clicking any other agent shows it single).</summary>
    private bool SplitActive =>
        SplitConfigured && _currentPage == "chat" && _selected != null
        && _splitPanes.Any(p => ReferenceEquals(p, _selected));

    private void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (SplitConfigured)
        {
            // Toggle: showing the split → turn the split off; set configured but viewing another
            // agent → jump back into the split.
            if (SplitActive) ExitSplit();
            else SelectTab(_splitPanes[0]);
            return;
        }
        if (_tabs.Count < 2 || _selected == null) return;   // button is disabled here anyway

        var other = _tabs.FirstOrDefault(t => !ReferenceEquals(t, _selected));
        if (other == null) return;

        _splitPanes.Clear();
        _splitPanes.Add(_selected);
        _splitPanes.Add(other);
        ResetPaneFractions();
        RefreshSplitBar();
        SwitchPage("chat");        // _selected is in the set → ApplyPaneLayout shows the split
        RefreshSplitButton();
        SaveWorkspace();
    }

    private void ExitSplit_Click(object sender, RoutedEventArgs e) => ExitSplit();

    private void ExitSplit()
    {
        _splitPanes.Clear();
        ResetPaneFractions();
        ApplyPaneLayout();
        RefreshSplitButton();
        SaveWorkspace();
    }

    /// <summary>Primary click: pane the next agent that isn't shown yet. The chevron's menu
    /// (built by <see cref="RefreshSplitBar"/>) picks a specific one instead.</summary>
    private void AddPane_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        var next = _tabs.FirstOrDefault(t => !_splitPanes.Contains(t));
        if (next != null) AddPane(next);
    }

    /// <summary>Appends an agent as a new pane, up to <see cref="MaxSplitPanes"/>. Called by the
    /// split bar's Add button and by a tab's "Add to split view" — including from single view, where
    /// the active agent takes the first slot so there's something to compare against.</summary>
    private void AddPane(ChatTabEntry entry)
    {
        if (_splitPanes.Count >= MaxSplitPanes) return;
        if (_splitPanes.Any(p => ReferenceEquals(p, entry))) return;

        if (_splitPanes.Count == 0)
        {
            var partner = _selected != null && !ReferenceEquals(_selected, entry)
                ? _selected
                : _tabs.FirstOrDefault(t => !ReferenceEquals(t, entry));
            if (partner == null) return;   // only one agent open — nothing to compare it with
            _splitPanes.Add(partner);
        }

        _splitPanes.Add(entry);
        ResetPaneFractions();
        RefreshSplitBar();
        SelectTab(entry);          // the new pane becomes active, so the split stays on screen
        RefreshSplitButton();
        SaveWorkspace();
    }

    /// <summary>Drops a pane. Falling to a single pane isn't a layout — it turns the split off and
    /// leaves you on the agent that survived.</summary>
    private void RemovePane(ChatTabEntry entry)
    {
        var idx = _splitPanes.FindIndex(p => ReferenceEquals(p, entry));
        if (idx < 0) return;
        _splitPanes.RemoveAt(idx);

        if (_splitPanes.Count < 2)
        {
            var survivor = _splitPanes.FirstOrDefault();
            _splitPanes.Clear();
            ResetPaneFractions();
            if (survivor != null) SelectTab(survivor);
            else ApplyPaneLayout();
            RefreshSplitButton();
            SaveWorkspace();
            return;
        }

        ResetPaneFractions();
        RefreshSplitBar();
        // Closing the pane you were focused on hands the focus to a pane that's still shown.
        if (ReferenceEquals(_selected, entry))
            SelectTab(_splitPanes[Math.Min(idx, _splitPanes.Count - 1)]);
        else
            ApplyPaneLayout();
        RefreshSplitButton();
        SaveWorkspace();
    }

    /// <summary>Re-points one pane at a different agent. Choosing an agent that already occupies
    /// another pane swaps the two, which is what the old two-combo bar did.</summary>
    private void SetPane(int paneIndex, ChatTabEntry entry)
    {
        if (paneIndex < 0 || paneIndex >= _splitPanes.Count) return;
        if (!_tabs.Contains(entry)) return;
        if (ReferenceEquals(_splitPanes[paneIndex], entry)) return;

        var existing = _splitPanes.FindIndex(p => ReferenceEquals(p, entry));
        if (existing >= 0) _splitPanes[existing] = _splitPanes[paneIndex];
        _splitPanes[paneIndex] = entry;

        RefreshSplitBar();
        SelectTab(entry);   // keep the split on screen
        RefreshSplitButton();
        SaveWorkspace();
    }

    /// <summary>Places the visible agent view(s) into pane cells and sizes the tracks. Single view
    /// collapses to one */* cell, so pages and the empty state fill it without knowing about panes.
    /// Setting Grid.Row/Grid.Column does NOT reparent, so WebViews are untouched.</summary>
    private void ApplyPaneLayout()
    {
        var split = SplitActive;
        var showingChat = _currentPage == "chat";
        int count = split ? _splitPanes.Count : 1;
        var (rows, cols) = PaneLayout.Shape(count);

        BuildPaneTracks(rows, cols);

        foreach (var tab in _tabs)
        {
            int pane = split ? _splitPanes.FindIndex(p => ReferenceEquals(p, tab)) : -1;
            var visible = showingChat && (split ? pane >= 0 : ReferenceEquals(tab, _selected));
            tab.View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            // Tracks interleave a divider between panes, so pane (r,c) lives at row 2r / column 2c.
            var (row, col) = pane >= 0 ? PaneLayout.Cell(pane, count) : (0, 0);
            Grid.SetRow(tab.View, row * 2);
            Grid.SetColumn(tab.View, col * 2);

            // Room for the header sharing this cell. Cleared in single view, where there is none.
            tab.View.Margin = pane >= 0 ? new Thickness(0, PaneHeaderHeight, 0, 0) : new Thickness(0);
        }

        BuildPaneHeaders(split, count);

        // The bar keeps the add/exit controls and the layout hint, but the per-pane pickers are gone
        // — swapping is done by dragging a tab onto the pane it should occupy.
        SplitBar.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Rebuilds the per-pane headers. Torn down and recreated wholesale on every layout pass, like
    /// the dividers: they are cheap, and a rebuild cannot drift out of step with the pane set.
    /// </summary>
    private void BuildPaneHeaders(bool split, int count)
    {
        foreach (var header in _paneHeaders) TabHost.Children.Remove(header);
        _paneHeaders.Clear();
        if (!split) return;

        for (int i = 0; i < _splitPanes.Count; i++)
        {
            var (row, col) = PaneLayout.Cell(i, count);
            var header = BuildPaneHeader(i, _splitPanes[i]);
            Grid.SetRow(header, row * 2);
            Grid.SetColumn(header, col * 2);
            TabHost.Children.Add(header);
            _paneHeaders.Add(header);
        }
    }

    /// <summary>Rebuilds TabHost's tracks for a pane grid of the given shape and recreates the
    /// dividers. Track layout is pane, divider, pane, … — 2c-1 columns and 2r-1 rows. Only track
    /// definitions and divider elements change here; agent views are never removed from the tree,
    /// so no WebView is torn down.</summary>
    private void BuildPaneTracks(int rows, int cols)
    {
        // Single view must NOT touch the fraction lists: they're the remembered divider positions,
        // and they have to survive visiting Settings or clicking a non-paned agent and coming back.
        bool paned = rows * cols > 1;
        if (paned) EnsureFractions(rows, cols);

        foreach (var grip in _paneGrips) TabHost.Children.Remove(grip);
        _paneGrips.Clear();

        TabHost.ColumnDefinitions.Clear();
        for (int c = 0; c < cols; c++)
        {
            TabHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(paned ? _colFractions[c] : 1, GridUnitType.Star)
            });
            if (c < cols - 1)
                TabHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        TabHost.RowDefinitions.Clear();
        for (int r = 0; r < rows; r++)
        {
            TabHost.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(paned ? _rowFractions[r] : 1, GridUnitType.Star)
            });
            if (r < rows - 1)
                TabHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        // A column divider spans every row and vice versa, so the 2×2 keeps a single cross of
        // dividers rather than four independent stubs.
        int trackRows = Math.Max(1, rows * 2 - 1);
        int trackCols = Math.Max(1, cols * 2 - 1);
        for (int c = 0; c < cols - 1; c++) AddPaneGrip(vertical: true, c, 2 * c + 1, trackRows);
        for (int r = 0; r < rows - 1; r++) AddPaneGrip(vertical: false, r, 2 * r + 1, trackCols);
    }

    /// <summary>
    /// One pane header: the agent's name and folder, a close button, and — the point of the whole
    /// rework — a drag source and a drop target.
    ///
    /// <para>Drag it onto another pane to swap the two. Drag a tab from the strip onto it to put
    /// that agent here instead. Both are the same operation on <see cref="_splitPanes"/>, which is
    /// an ordered list, so the model needed no changes at all.</para>
    /// </summary>
    private Border BuildPaneHeader(int index, ChatTabEntry pane)
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var isActive = ReferenceEquals(pane, _selected);

        var label = new TextBlock
        {
            Text = pane.View.Session.Title,
            // 13 to match the tab strip: this header is doing a tab's job, so it should carry the
            // same weight as one rather than reading as a caption above the pane.
            FontSize = 13,
            FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = isActive ? accent : (SolidColorBrush)Application.Current.Resources["MandoTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var paneRef = pane;
        close.Click += (_, _) => DispatcherQueue.TryEnqueue(() => RemovePane(paneRef));
        ToolTipService.SetToolTip(close, "Remove from split view");

        // The move cursor covers the name only, not the close button beside it — same reason as the
        // tab strip: a move cursor over something you click promises a drag that will not start.
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "\uE8BD", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        // Name only. The agent's own header already shows the folder it works in, directly below
        // this — repeating it here spends pane width saying the same thing twice.
        content.Children.Add(label);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameHandle = new Controls.DragHandleGrid();
        nameHandle.Children.Add(content);
        grid.Children.Add(nameHandle);
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        var header = new Border
        {
            Child = grid,
            Height = PaneHeaderHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(9, 0, 4, 0),
            Background = (SolidColorBrush)Application.Current.Resources["MandoPanelBrush"],
            BorderBrush = isActive ? accent : (SolidColorBrush)Application.Current.Resources["MandoBorderBrush"],
            // Accent along the BOTTOM edge on the active pane: it reads as the header belonging to
            // the view under it rather than as a box floating above it.
            BorderThickness = new Thickness(0, 0, 0, isActive ? 2 : 1),
            Tag = index,
            CanDrag = true,
            AllowDrop = true,
        };

        header.Tapped += (_, _) => SelectTab(paneRef);
        header.DragStarting += (_, args) => BeginAgentDrag(args, paneRef);
        header.DropCompleted += (_, _) => EndAgentDrag();
        header.DragOver += PaneTarget_DragOver;
        header.Drop += (sender, args) => DropAgentOnPane(sender, args);
        ToolTipService.SetToolTip(header, $"{pane.View.Session.Title} — drag onto another pane to swap");
        return header;
    }

    // ============================================================
    // Dragging an agent into a pane
    // ============================================================

    /// <summary>
    /// The clipboard format an agent drag carries — a session's PersistKey.
    ///
    /// <para>A private format rather than <c>Text</c> on purpose: the chat surface already accepts
    /// dropped files and text as attachments, and a bare string would make an agent drag look like
    /// something to attach. With a format of its own, neither drag can be mistaken for the other and
    /// the two features need know nothing about each other.</para>
    /// </summary>
    private const string AgentDragFormat = "MandoCode/AgentKey";

    /// <summary>The agent currently being dragged, or null. Held because a DataView's custom formats
    /// can only be read asynchronously, and DragOver has to decide synchronously whether to accept.</summary>
    private ChatTabEntry? _draggingAgent;

    /// <summary>Starts an agent drag from either a strip tab or a pane header.</summary>
    private void BeginAgentDrag(DragStartingEventArgs args, ChatTabEntry entry)
    {
        _draggingAgent = entry;
        args.Data.SetText(entry.View.Session.PersistKey);
        args.Data.SetData(AgentDragFormat, entry.View.Session.PersistKey);
        args.Data.RequestedOperation = DataPackageOperation.Move;
        ShowPaneDropTargets(entry);
    }

    private void EndAgentDrag()
    {
        _draggingAgent = null;
        HidePaneDropTargets();
    }

    /// <summary>
    /// Tracks which half of the surface the pointer is over and previews that outcome. Crossing the
    /// midline moves the highlight and rewrites the caption, so which side the dragged agent takes
    /// is chosen by aiming rather than dictated — dropping on the left means "put this one on the
    /// left", which is what the gesture looks like it should mean.
    /// </summary>
    private void StartSplitTarget_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingAgent == null || _selected == null || sender is not FrameworkElement surface) return;

        var onLeft = e.GetPosition(surface).X < surface.ActualWidth / 2;
        if (_startSplitPreview != null)
            _startSplitPreview.HorizontalAlignment = onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        if (onLeft != _startSplitDraggedOnLeft || _startSplitCaption?.Text is null or "")
        {
            _startSplitDraggedOnLeft = onLeft;
            if (_startSplitCaption != null)
                _startSplitCaption.Text = onLeft
                    ? $"{_draggingAgent.View.Session.Title} on the left, {_selected.View.Session.Title} on the right"
                    : $"{_selected.View.Session.Title} on the left, {_draggingAgent.View.Session.Title} on the right";
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.Caption = onLeft ? "Place on the left" : "Place on the right";
        e.Handled = true;
    }

    private void PaneTarget_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingAgent == null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.Caption = CaptionFor(sender);
        e.Handled = true;
    }

    private string CaptionFor(object target)
    {
        var index = (target as FrameworkElement)?.Tag as int?;
        if (_draggingAgent == null) return "Move here";

        // Single view: the drop starts a split rather than filling a pane, so say that instead of
        // naming an occupant there isn't one of.
        if (index == StartSplitTag)
            return _selected == null
                ? "Open beside the current agent"
                : $"Open beside {_selected.View.Session.Title}";

        if (index is not { } i || i < 0 || i >= _splitPanes.Count) return "Move here";
        var occupant = _splitPanes[i];
        return ReferenceEquals(occupant, _draggingAgent)
            ? $"{_draggingAgent.View.Session.Title} is already here"
            : _splitPanes.Contains(_draggingAgent)
                ? $"Swap with {occupant.View.Session.Title}"
                : $"Replace {occupant.View.Session.Title}";
    }

    /// <summary>
    /// Puts the dragged agent in the pane it was dropped on.
    ///
    /// <para>Both gestures are one operation on an ordered list. If the dragged agent is already
    /// paned, the two exchange slots — a swap, because dropping onto an occupied pane can only mean
    /// "these two should trade places". If it is not, it takes the slot and the previous occupant
    /// leaves the split, which is what dragging a tab in from the strip visibly does.</para>
    /// </summary>
    private void DropAgentOnPane(object sender, DragEventArgs e)
    {
        var dragged = _draggingAgent;
        var wasSelected = _selected;
        EndAgentDrag();
        if (dragged == null) return;
        if ((sender as FrameworkElement)?.Tag is not int target) return;

        if (target == StartSplitTag)
        {
            if (wasSelected == null || ReferenceEquals(wasSelected, dragged)) return;
            _splitPanes.Clear();
            // Pane order follows the half the pointer was over when you let go.
            if (_startSplitDraggedOnLeft)
            {
                _splitPanes.Add(dragged);
                _splitPanes.Add(wasSelected);
            }
            else
            {
                _splitPanes.Add(wasSelected);
                _splitPanes.Add(dragged);
            }
            ResetPaneFractions();
            e.Handled = true;

            // Selection stays where it was. AddPane moves it to the newcomer, which is right when
            // you asked for that agent by name — but here you dropped something NEXT TO what you
            // were reading, so being moved off it would undo half the gesture.
            DispatcherQueue.TryEnqueue(() =>
            {
                SelectTab(wasSelected);
                ApplyPaneLayout();
                RefreshTabStrip();
                RefreshSplitBar();
                RefreshSplitButton();
                SaveWorkspace();
            });
            return;
        }

        if (target < 0 || target >= _splitPanes.Count) return;

        var existing = _splitPanes.FindIndex(p => ReferenceEquals(p, dragged));
        if (existing == target) return;   // dropped where it already is

        if (existing >= 0)
            (_splitPanes[existing], _splitPanes[target]) = (_splitPanes[target], _splitPanes[existing]);
        else
            _splitPanes[target] = dragged;

        e.Handled = true;
        // Deferred for the same reason the chip menus were: this restructures the visual tree, which
        // is not legal from inside the event that is still delivering the drop.
        DispatcherQueue.TryEnqueue(() =>
        {
            SelectTab(dragged);
            ApplyPaneLayout();
            RefreshTabStrip();
            RefreshSplitBar();
            SaveWorkspace();
        });
    }

    /// <summary>
    /// Reveals a drop target over each pane for the duration of a drag.
    ///
    /// <para>Full-pane rather than header-only, which needs the trick the file-attachment drop
    /// already uses: a WebView2 swallows drags over its own surface, but an XAML element drawn ON TOP
    /// of it takes the drag back, because the OS retargets to whatever is topmost. Since an agent
    /// drag starts in XAML we can raise these the moment it begins, without waiting to be told by the
    /// WebView the way an incoming file drag has to be.</para>
    ///
    /// <para>They also answer discoverability: the droppable places light up as soon as you pick a
    /// tab up, so the gesture does not have to be known in advance to be found.</para>
    /// </summary>
    private void ShowPaneDropTargets(ChatTabEntry dragged)
    {
        HidePaneDropTargets();

        // Not split yet: dropping onto the chat area STARTS one, keeping the agent you were reading
        // on the left and putting the dragged agent beside it. That is the gesture's plain meaning —
        // "show me this one as well" — and requiring a trip to the Split button first to express it
        // would be the kind of ceremony dragging exists to remove.
        if (!SplitActive)
        {
            if (_currentPage != "chat" || _selected == null || ReferenceEquals(_selected, dragged)) return;
            AddSingleViewDropTarget(dragged);
            return;
        }

        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        int count = _splitPanes.Count;
        for (int i = 0; i < count; i++)
        {
            var (row, col) = PaneLayout.Cell(i, count);
            var occupant = _splitPanes[i];
            var isSelf = ReferenceEquals(occupant, dragged);

            var caption = new TextBlock
            {
                Text = isSelf ? "Already here" : CaptionFor(i, occupant, dragged),
                Foreground = accent,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var target = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(isSelf ? (byte)0x30 : (byte)0x88, 0, 0, 0)),
                BorderBrush = accent,
                BorderThickness = new Thickness(isSelf ? 0 : 2),
                Margin = new Thickness(0, PaneHeaderHeight, 0, 0),
                AllowDrop = true,
                Tag = i,
            };
            target.Children.Add(caption);
            target.DragOver += PaneTarget_DragOver;
            target.Drop += DropAgentOnPane;

            Grid.SetRow(target, row * 2);
            Grid.SetColumn(target, col * 2);
            TabHost.Children.Add(target);
            _paneDropTargets.Add(target);
        }
    }

    private string CaptionFor(int index, ChatTabEntry occupant, ChatTabEntry dragged) =>
        _splitPanes.Contains(dragged)
            ? $"Swap with {occupant.View.Session.Title}"
            : $"Replace {occupant.View.Session.Title}";

    /// <summary>
    /// The one drop target shown in single view. Its hit area is the WHOLE chat surface, but only
    /// the right half is drawn — the outcome is fixed, so the highlight shows where the dragged
    /// agent will land while the generous target keeps a slightly-off aim from doing nothing.
    /// </summary>
    private void AddSingleViewDropTarget(ChatTabEntry dragged)
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];

        var preview = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                // Rewritten on every DragOver to name the resulting left/right order.
                Text = $"{_selected!.View.Session.Title} on the left, {dragged.View.Session.Title} on the right",
                Foreground = accent,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        _startSplitPreview = preview;
        _startSplitCaption = (TextBlock)preview.Child;
        _startSplitDraggedOnLeft = false;   // default until the pointer says otherwise

        var target = new Grid
        {
            AllowDrop = true,
            Tag = StartSplitTag,
            // A Grid with a NULL Background takes no part in hit testing, so it would draw the
            // preview below and then never receive the drop — which is exactly what it did. A brush
            // is what makes the surface real to the pointer; Transparent would be enough, and the
            // faint scrim also dims the half that is about to be given away.
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0, 0, 0)),
        };
        target.Children.Add(preview);
        target.SizeChanged += (_, e) => preview.Width = e.NewSize.Width / 2;
        target.DragOver += StartSplitTarget_DragOver;
        target.Drop += DropAgentOnPane;

        // Spans every track so it covers the surface whatever shape the grid was left in.
        Grid.SetRow(target, 0);
        Grid.SetColumn(target, 0);
        Grid.SetRowSpan(target, 3);
        Grid.SetColumnSpan(target, 5);
        TabHost.Children.Add(target);
        _paneDropTargets.Add(target);
    }

    /// <summary>Tag on the single-view target: this drop CREATES the split rather than filling a
    /// pane in one, so it cannot be a pane index.</summary>
    private const int StartSplitTag = -1;

    /// <summary>The half of the surface the pointer is currently over, and therefore where the
    /// dragged agent will land. Updated as the pointer moves so the choice is made by aiming rather
    /// than by accepting a fixed outcome.</summary>
    private bool _startSplitDraggedOnLeft;

    /// <summary>The highlight showing which half is about to be taken; moved as the pointer crosses
    /// the midline.</summary>
    private Border? _startSplitPreview;
    private TextBlock? _startSplitCaption;

    private void HidePaneDropTargets()
    {
        foreach (var target in _paneDropTargets) TabHost.Children.Remove(target);
        _paneDropTargets.Clear();
        _startSplitPreview = null;
        _startSplitCaption = null;
    }

    private readonly List<Grid> _paneDropTargets = new();

    /// <summary>Creates one divider. <paramref name="index"/> goes in Tag — it's the fraction-list
    /// slot the drag repartitions.</summary>
    private void AddPaneGrip(bool vertical, int index, int track, int span)
    {
        var grip = new Controls.ResizeGrip
        {
            // A Vertical grip bar resizes horizontally (↔) — it's the one that sits between columns.
            GripOrientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Background = (SolidColorBrush)Application.Current.Resources["MandoBorderBrush"],
            Tag = index,
        };

        if (vertical)
        {
            grip.Width = 6;
            grip.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(grip, track);
            Grid.SetRow(grip, 0);
            Grid.SetRowSpan(grip, span);
            grip.PointerPressed += PaneGrip_PointerPressed;
            grip.PointerMoved += PaneColumnGrip_PointerMoved;
            grip.PointerReleased += PaneGrip_PointerReleased;
        }
        else
        {
            grip.Height = 6;
            grip.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(grip, track);
            Grid.SetColumn(grip, 0);
            Grid.SetColumnSpan(grip, span);
            grip.PointerPressed += PaneGrip_PointerPressed;
            grip.PointerMoved += PaneRowGrip_PointerMoved;
            grip.PointerReleased += PaneGrip_PointerReleased;
        }

        TabHost.Children.Add(grip);
        _paneGrips.Add(grip);
    }

    /// <summary>Keeps the fraction lists matching the layout shape. A shape change (pane added or
    /// removed) resets to equal splits; an unchanged shape keeps whatever the user dragged.</summary>
    private void EnsureFractions(int rows, int cols)
    {
        _colFractions = PaneLayout.Fit(_colFractions, cols);
        _rowFractions = PaneLayout.Fit(_rowFractions, rows);
    }

    private void ResetPaneFractions()
    {
        _colFractions.Clear();
        _rowFractions.Clear();
    }

    /// <summary>Rebuilds one chip per pane. Each chip's dropdown re-points that pane; its × drops
    /// the pane. Chips are MenuFlyout-based and deliberately NOT ComboBoxes: adding containers
    /// directly as ComboBox items and rebuilding them makes WinUI throw COMException 0x80070490
    /// "Element not found" on the next selection, which is what the old two-picker bar had to work
    /// around.</summary>
    private void RefreshSplitBar()
    {
        if (_syncingSplitBar) return;
        _syncingSplitBar = true;
        try
        {
            // No per-pane chips any more. Naming a pane by its ORDINAL — "pane 2 is Jetik" — made
            // you hold the mapping in your head, which is the work the split is supposed to save.
            // The pane headers say it in place instead, and dragging replaces the pickers.
            // Add-pane picker: only agents that aren't already shown — an agent can't occupy two
            // panes, so listing one would be a no-op. Deferred a tick like every other split
            // mutation (it restructures the visual tree and rebuilds this bar).
            AddPaneMenu.Items.Clear();
            foreach (var tab in _tabs.Where(t => !_splitPanes.Contains(t)))
            {
                var target = tab;
                var item = new MenuFlyoutItem { Text = target.View.Session.Title };
                item.Click += (_, _) => DispatcherQueue.TryEnqueue(() => AddPane(target));
                AddPaneMenu.Items.Add(item);
            }

            AddPaneButton.IsEnabled = _splitPanes.Count < MaxSplitPanes && AddPaneMenu.Items.Count > 0;

            var (rows, cols) = PaneLayout.Shape(_splitPanes.Count);
            PaneLayoutHint.Text = _splitPanes.Count < 2 ? ""
                : rows == 1 ? $"{cols} across"
                : $"{rows}×{cols} grid";
        }
        finally { _syncingSplitBar = false; }
    }

    /// <summary>Keeps the pane set valid after the agent set changes. Panes whose agent was
    /// closed drop out; falling below two panes turns the split off entirely.</summary>
    private void ValidateSplit()
    {
        if (_splitPanes.Count == 0) return;   // no split configured

        _splitPanes.RemoveAll(p => !_tabs.Contains(p));
        if (_splitPanes.Count < 2)
        {
            _splitPanes.Clear();
            ResetPaneFractions();
            ApplyPaneLayout();
            RefreshSplitButton();
            return;
        }
        RefreshSplitBar();
        ApplyPaneLayout();
        RefreshSplitButton();
    }

    /// <summary>Re-establishes a saved pane set once the tabs exist. Panes are matched by
    /// persist-key, not index, so tabs skipped at restore (project folder gone) simply drop out of
    /// the set instead of shifting every other pane.</summary>
    private void RestoreSplitLayout(WorkspaceShape shape)
    {
        if (shape.SplitPanes is not { Count: >= 2 }) return;

        _splitPanes.Clear();
        foreach (var key in shape.SplitPanes)
        {
            if (_splitPanes.Count >= MaxSplitPanes) break;
            var tab = _tabs.FirstOrDefault(t => t.View.Session.PersistKey == key);
            if (tab != null && !_splitPanes.Contains(tab)) _splitPanes.Add(tab);
        }
        if (_splitPanes.Count < 2)
        {
            _splitPanes.Clear();
            return;
        }

        // Saved divider positions only apply if they still describe this shape.
        var (rows, cols) = PaneLayout.Shape(_splitPanes.Count);
        if (shape.PaneColumnFractions is { } cf && cf.Count == cols && cf.Sum() > 0)
            _colFractions = new List<double>(cf);
        if (shape.PaneRowFractions is { } rf && rf.Count == rows && rf.Sum() > 0)
            _rowFractions = new List<double>(rf);

        RefreshSplitBar();
        ApplyPaneLayout();
        RefreshSplitButton();
    }

    private void RefreshSplitButton()
    {
        SplitButton.IsEnabled = SplitConfigured || _tabs.Count >= 2;
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var normal = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        // Accent whenever a split is configured — even while viewing a non-paned agent — so
        // it reads as "split view is on; click a paned tab (or me) to see it."
        SplitButtonIcon.Foreground = SplitConfigured ? accent : normal;
    }

    // ---- divider drag ----------------------------------------------------------
    // Each divider repartitions ONLY the two panes either side of it: their combined fraction is
    // held constant, so dragging one divider never nudges a pane further along the axis.

    private void PaneGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingPaneGrip = true;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void PaneColumnGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPaneGrip) return;
        if (sender is not FrameworkElement { Tag: int i }) return;
        double w = TabHost.ActualWidth;
        if (w <= 0) return;

        PaneLayout.Repartition(_colFractions, i, e.GetCurrentPoint(TabHost).Position.X / w);
        ApplyPaneTrackSizes();
    }

    private void PaneRowGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPaneGrip) return;
        if (sender is not FrameworkElement { Tag: int i }) return;
        double h = TabHost.ActualHeight;
        if (h <= 0) return;

        PaneLayout.Repartition(_rowFractions, i, e.GetCurrentPoint(TabHost).Position.Y / h);
        ApplyPaneTrackSizes();
    }

    private void PaneGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPaneGrip) return;
        _draggingPaneGrip = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SaveWorkspace();   // divider positions are part of the remembered layout
    }

    /// <summary>Pushes the current fractions onto the existing tracks — no track rebuild and no
    /// divider recreation, so it's cheap enough to run on every pointer move.</summary>
    private void ApplyPaneTrackSizes()
    {
        for (int c = 0; c < _colFractions.Count; c++)
        {
            int track = c * 2;
            if (track < TabHost.ColumnDefinitions.Count)
                TabHost.ColumnDefinitions[track].Width = new GridLength(_colFractions[c], GridUnitType.Star);
        }
        for (int r = 0; r < _rowFractions.Count; r++)
        {
            int track = r * 2;
            if (track < TabHost.RowDefinitions.Count)
                TabHost.RowDefinitions[track].Height = new GridLength(_rowFractions[r], GridUnitType.Star);
        }
    }

    /// <summary>Paints the custom chat background image behind the empty state, so closing every
    /// agent leaves the same backdrop you'd see behind a transcript — same file and opacity. Hidden
    /// when there's no image set, or when an agent is open (its own WebView paints it then). Loaded
    /// via a StorageFile stream, the reliable path for an arbitrary filesystem image in unpackaged
    /// WinUI; best-effort, so a missing/locked file just falls back to the flat themed colour.</summary>
    private async Task RefreshEmptyBackgroundAsync()
    {
        var show = _currentPage == "chat" && _tabs.Count == 0;
        var file = ThemeManager.ChatBackgroundFile;
        if (!show || string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            EmptyBgImage.Visibility = Visibility.Collapsed;
            EmptyBgImage.Source = null;
            return;
        }
        try
        {
            var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
            using var stream = await sf.OpenReadAsync();
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(stream);
            EmptyBgImage.Source = bmp;
            EmptyBgImage.Opacity = ThemeManager.ChatBackgroundOpacity;
            EmptyBgImage.Visibility = Visibility.Visible;
        }
        catch
        {
            EmptyBgImage.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshTabStrip()
    {
        // Piggybacks on the tab strip because the two answer the same question — who is open and
        // what are they doing — and this already runs on every event that changes either.
        RefreshAgentDirectory();

        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var border = (SolidColorBrush)Application.Current.Resources["MandoBorderBrush"];
        var dim = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var background = (SolidColorBrush)Application.Current.Resources["MandoBackgroundBrush"];
        var transparent = new SolidColorBrush(Colors.Transparent);

        ChatTabEntry? pending = null;

        foreach (var tab in _tabs)
        {
            // An agent already on screen does not also need a tab. Its name is on the pane header
            // now, so the strip goes back to being a list of what is NOT currently visible — which
            // is also what makes it obvious that dragging one in is the way to show it.
            var paned = SplitActive && _splitPanes.Any(p => ReferenceEquals(p, tab));
            tab.Header.Visibility = paned ? Visibility.Collapsed : Visibility.Visible;

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
        RefreshSplitButton();
        // Chips outline the active pane and carry agent titles, so they follow selection and renames.
        if (SplitConfigured) RefreshSplitBar();
        LayoutTabStrip();
    }

    // Tabs stay a comfortable width when there's room, and only shrink once enough agents are open
    // that they'd otherwise overflow — down to a floor, past which the strip scrolls instead.
    private const double TabComfortableWidth = 200;
    private const double TabMinWidth = 104;

    private void LayoutTabStrip()
    {
        // Paned agents have no tab in the strip, so they must not be counted when dividing up the
        // width — otherwise the tabs that ARE shown come out narrower than they need to be, and a
        // 4-pane split would squeeze the remainder for no reason.
        var shown = _tabs.Where(t => t.Header.Visibility == Visibility.Visible).ToList();
        int count = shown.Count;
        if (count == 0) return;

        // The visible strip is the scroller's viewport; a later SizeChanged fixes up the first
        // pass if it hasn't been measured yet (ActualWidth == 0 during early layout).
        double viewport = TabScroller.ActualWidth;   // tabs only — the add button now lives outside
        if (viewport <= 0) return;

        double spacing = 4 * Math.Max(0, count - 1);         // 4px between adjacent tabs
        double avail = viewport - spacing - 8;               // margin so rounding never forces a scrollbar

        double per = Math.Max(TabMinWidth, Math.Min(TabComfortableWidth, avail / count));
        foreach (var tab in shown)
            tab.Header.Width = per;
    }

    private void TabScroller_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTabStrip();

    // Mouse wheel scrolls the strip horizontally when there are more tabs than fit — a convenience
    // on top of the visible scrollbar (which sits in a reserved bottom lane so it never overlaps
    // the tabs). Touchpad / touch horizontal scrolling works natively.
    private void TabScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (TabScroller.ScrollableWidth <= 0) return;   // everything fits; nothing to scroll
        var delta = e.GetCurrentPoint(TabScroller).Properties.MouseWheelDelta;
        TabScroller.ChangeView(TabScroller.HorizontalOffset - delta, null, null);
        e.Handled = true;
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

}
