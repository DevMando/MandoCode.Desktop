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
        }

        SplitBar.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
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
            PaneChips.Children.Clear();
            for (int i = 0; i < _splitPanes.Count; i++)
                PaneChips.Children.Add(BuildPaneChip(i, _splitPanes[i]));

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

    /// <summary>One pane chip: position, agent name, a picker, and a remove button. Both menu
    /// actions are deferred to the next dispatcher tick — the reaction restructures the visual tree
    /// (moves a ChatTabView between grid cells) and rebuilds this bar, neither of which is legal
    /// from inside a flyout's click handler.</summary>
    private Border BuildPaneChip(int index, ChatTabEntry pane)
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var border = (SolidColorBrush)Application.Current.Resources["MandoBorderBrush"];
        var dim = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var isActive = ReferenceEquals(pane, _selected);

        var ordinal = new TextBlock
        {
            Text = (index + 1).ToString(),
            FontSize = 10,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = new TextBlock
        {
            Text = pane.View.Session.Title,
            FontSize = 12,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = isActive ? accent : dim,
        };

        var picker = new Button
        {
            Padding = new Thickness(2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 9 },   // ChevronDown
        };
        ToolTipService.SetToolTip(picker, "Show a different agent in this pane");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Pane {index + 1} agent");

        var menu = new MenuFlyout();
        foreach (var tab in _tabs)
        {
            var target = tab;
            var item = new MenuFlyoutItem { Text = target.View.Session.Title };
            if (ReferenceEquals(target, pane))
                item.Icon = new FontIcon { Glyph = "" };   // check — the current occupant
            item.Click += (_, _) => DispatcherQueue.TryEnqueue(() => SetPane(index, target));
            menu.Items.Add(item);
        }
        picker.Flyout = menu;

        var remove = new Button
        {
            Padding = new Thickness(2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 9 },   // close
        };
        ToolTipService.SetToolTip(remove, "Remove this pane");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove pane {index + 1}");
        remove.Click += (_, _) => DispatcherQueue.TryEnqueue(() => RemovePane(pane));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(ordinal);
        row.Children.Add(label);
        row.Children.Add(picker);
        row.Children.Add(remove);

        return new Border
        {
            Child = row,
            Padding = new Thickness(9, 3, 5, 3),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            // The active pane is outlined, matching how the tab strip marks the selected agent.
            BorderBrush = isActive ? accent : border,
            Background = new SolidColorBrush(Colors.Transparent),
        };
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
        int count = _tabs.Count;
        if (count == 0) return;

        // The visible strip is the scroller's viewport; a later SizeChanged fixes up the first
        // pass if it hasn't been measured yet (ActualWidth == 0 during early layout).
        double viewport = TabScroller.ActualWidth;   // tabs only — the add button now lives outside
        if (viewport <= 0) return;

        double spacing = 4 * Math.Max(0, count - 1);         // 4px between adjacent tabs
        double avail = viewport - spacing - 8;               // margin so rounding never forces a scrollbar

        double per = Math.Max(TabMinWidth, Math.Min(TabComfortableWidth, avail / count));
        foreach (var tab in _tabs)
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
