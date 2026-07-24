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
    // Split / compare view — two agents side by side. The compare PAIR (_compareA left, _compareB
    // right) is a remembered, explicit choice: set only by the Split button and the compare-bar
    // pickers, NEVER by clicking a tab. The split is shown whenever the active tab (_selected) is one
    // of the pair; clicking any other tab shows that agent normally while the pair waits, and
    // clicking a paired tab brings the split back. Both panes are ordinary tab views moved between
    // grid columns via ApplyPaneLayout — never reparented, so their WebViews survive.
    // ============================================================

    private ChatTabEntry? _compareA;   // left pane
    private ChatTabEntry? _compareB;   // right pane
    private double _splitLeftFraction = 0.5;   // divider position, preserved across page visits
    private bool _syncingSplitCombos;
    private bool _draggingPane;

    /// <summary>A valid, distinct compare pair is configured (both agents still open).</summary>
    private bool HasComparePair =>
        _compareA != null && _compareB != null
        && _tabs.Contains(_compareA) && _tabs.Contains(_compareB)
        && !ReferenceEquals(_compareA, _compareB);

    /// <summary>The split is actually being shown right now: a pair exists, we're on the chat page,
    /// and the active tab is one of the two paired agents (clicking any other agent shows it single).</summary>
    private bool SplitActive =>
        HasComparePair && _currentPage == "chat" && _selected != null
        && (ReferenceEquals(_selected, _compareA) || ReferenceEquals(_selected, _compareB));

    private void SplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (HasComparePair)
        {
            // Toggle: showing the split → turn compare off; pair configured but viewing another
            // agent → jump back into the split.
            if (SplitActive) ExitSplit();
            else if (_compareA != null) SelectTab(_compareA);
            return;
        }
        if (_tabs.Count < 2 || _selected == null) return;   // button is disabled here anyway

        _compareA = _selected;
        _compareB = _tabs.FirstOrDefault(t => !ReferenceEquals(t, _selected));
        RefreshSplitCombos();
        SwitchPage("chat");        // _selected is in the pair → ApplyPaneLayout shows the split
        RefreshSplitButton();
    }

    private void ExitSplit_Click(object sender, RoutedEventArgs e) => ExitSplit();

    private void ExitSplit()
    {
        _compareA = null;
        _compareB = null;
        ApplyPaneLayout();
        RefreshSplitButton();
    }

    /// <summary>Places the visible agent view(s) into columns and sizes them. Single view: column 0
    /// fills (divider + right column collapse to 0). Split: _compareA in column 0, _compareB in
    /// column 2, divider between. Setting Grid.Column does NOT reparent, so WebViews are untouched.</summary>
    private void ApplyPaneLayout()
    {
        var split = SplitActive;
        var showingChat = _currentPage == "chat";

        foreach (var tab in _tabs)
        {
            bool inPair = ReferenceEquals(tab, _compareA) || ReferenceEquals(tab, _compareB);
            var visible = showingChat && (split ? inPair : ReferenceEquals(tab, _selected));
            tab.View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(tab.View, split && ReferenceEquals(tab, _compareB) ? 2 : 0);
        }

        if (split)
        {
            PaneLeftCol.Width = new GridLength(_splitLeftFraction, GridUnitType.Star);
            PaneRightCol.Width = new GridLength(1 - _splitLeftFraction, GridUnitType.Star);
            PaneSplitCol.Width = GridLength.Auto;
            PaneSplitter.Visibility = Visibility.Visible;
            SplitBar.Visibility = Visibility.Visible;
        }
        else
        {
            PaneLeftCol.Width = new GridLength(1, GridUnitType.Star);
            PaneSplitCol.Width = new GridLength(0);
            PaneRightCol.Width = new GridLength(0);
            PaneSplitter.Visibility = Visibility.Collapsed;
            SplitBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-fills the two pane pickers and re-selects the sides. Items are plain STRINGS
    /// (agent titles) selected by INDEX into <see cref="_tabs"/> — deliberately NOT ComboBoxItem
    /// objects: adding containers directly as items and rebuilding them makes WinUI's ComboBox throw
    /// COMException 0x80070490 "Element not found" on the next selection. Each combo gets its own
    /// list instance (a shared ItemsSource across two ComboBoxes is asking for trouble).</summary>
    private void RefreshSplitCombos()
    {
        _syncingSplitCombos = true;
        SplitLeftCombo.ItemsSource = _tabs.Select(t => t.View.Session.Title).ToList();
        SplitRightCombo.ItemsSource = _tabs.Select(t => t.View.Session.Title).ToList();
        SplitLeftCombo.SelectedIndex = _compareA == null ? -1 : _tabs.IndexOf(_compareA);
        SplitRightCombo.SelectedIndex = _compareB == null ? -1 : _tabs.IndexOf(_compareB);
        _syncingSplitCombos = false;
    }

    // Both pickers defer their ENTIRE reaction to the next dispatcher tick. A ComboBox raises
    // SelectionChanged from inside a layout pass, and the reaction restructures the visual tree
    // (moves a ChatTabView + its WebView between grid columns) and rebuilds the pickers — both
    // illegal mid-layout / mid-event and the source of the App-level crash. Off the event, on a
    // clean tick, they're safe. Picking an agent for one pane that's already the other pane swaps
    // the two. The chosen agent becomes active, so the split stays on screen.
    private void SplitLeftCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSplitCombos) return;
        var idx = SplitLeftCombo.SelectedIndex;
        if (idx < 0 || idx >= _tabs.Count) return;
        var entry = _tabs[idx];
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tabs.Contains(entry) || ReferenceEquals(entry, _compareA)) return;
            if (ReferenceEquals(entry, _compareB)) _compareB = _compareA;   // swap sides
            _compareA = entry;
            RefreshSplitCombos();
            SelectTab(entry);   // make the left pane active so the split stays shown
        });
    }

    private void SplitRightCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSplitCombos) return;
        var idx = SplitRightCombo.SelectedIndex;
        if (idx < 0 || idx >= _tabs.Count) return;
        var entry = _tabs[idx];
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tabs.Contains(entry) || ReferenceEquals(entry, _compareB)) return;
            if (ReferenceEquals(entry, _compareA)) _compareA = _compareB;   // swap sides
            _compareB = entry;
            RefreshSplitCombos();
            SelectTab(entry);   // make the right pane active so the split stays shown
        });
    }

    /// <summary>Keeps the compare pair valid after the agent set changes. If either paired agent was
    /// closed the pair is dropped (compare turns off); otherwise the pickers are resynced.</summary>
    private void ValidateSplit()
    {
        if (_compareA == null && _compareB == null) return;   // no compare configured
        if (!HasComparePair)
        {
            _compareA = null;
            _compareB = null;
            ApplyPaneLayout();
            RefreshSplitButton();
            return;
        }
        RefreshSplitCombos();
        ApplyPaneLayout();
        RefreshSplitButton();
    }

    private void RefreshSplitButton()
    {
        SplitButton.IsEnabled = HasComparePair || _tabs.Count >= 2;
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var normal = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        // Accent whenever a compare pair is configured — even while viewing a non-paired agent — so
        // it reads as "compare is on; click a paired tab (or me) to see it."
        SplitButtonIcon.Foreground = HasComparePair ? accent : normal;
    }

    // ---- divider drag: repartition the two panes' star widths by pointer X over TabHost ----
    private void PaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingPane = true;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void PaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPane) return;
        var w = TabHost.ActualWidth;
        if (w <= 0) return;
        var x = e.GetCurrentPoint(TabHost).Position.X;
        _splitLeftFraction = Math.Clamp(x / w, 0.2, 0.8);   // keep both panes usable
        PaneLeftCol.Width = new GridLength(_splitLeftFraction, GridUnitType.Star);
        PaneRightCol.Width = new GridLength(1 - _splitLeftFraction, GridUnitType.Star);
    }

    private void PaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPane) return;
        _draggingPane = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
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
