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
    // Snapshots panel — global (the store is app-wide), toggled from the rail. Docked left at
    // ~37% width so the active chat stays visible; Import arms the selected agent's next message.
    // ============================================================

    private void NavSnapshots_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsPanelOpen) CloseLeftPanel();
        else OpenSnapshots();
    }

    private void CloseSnapshots_Click(object sender, RoutedEventArgs e) => CloseLeftPanel();

    private void OpenSnapshots()
    {
        MarkSnapshotsSeen();   // opening the panel IS reading it — clear the unread badge
        PopulateSnapshots();
        ShowLeftPanel(LeftPanel.Snapshots);
    }

    /// <summary>Shows one of the docked panels (Snapshots/History/Notes), swapping if another was
    /// already up (the column stays out — only the contents change) and sliding it in otherwise.</summary>
    private void ShowLeftPanel(LeftPanel which)
    {
        bool wasOpen = _leftPanel != LeftPanel.None;
        _leftPanel = which;
        SnapshotsPanel.Visibility = which == LeftPanel.Snapshots ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = which == LeftPanel.History ? Visibility.Visible : Visibility.Collapsed;
        NotesPanel.Visibility = which == LeftPanel.Notes ? Visibility.Visible : Visibility.Collapsed;
        RefreshNavIcons();
        if (wasOpen) return;   // column already at width — contents swapped, no re-slide

        // Target ~37% of the content area (everything right of the 48px rail), matching the old
        // 0.6* / 1* split. Computed in pixels at open time so the tween can drive the column.
        double target = Math.Max(320, (Root.ActualWidth - 48) * 0.375);
        AnimateLeftColumn(target, hideOnDone: null);
    }

    private void CloseLeftPanel()
    {
        var toHide = _leftPanel switch
        {
            LeftPanel.Snapshots => (FrameworkElement)SnapshotsPanel,
            LeftPanel.History => HistoryPanel,
            LeftPanel.Notes => NotesPanel,
            _ => null,
        };
        // Leaving the Notes panel commits whatever is in the editor — a jot you can't see is a jot
        // you'd assume was saved (autosave is on a debounce, so it may not have fired yet).
        if (_leftPanel == LeftPanel.Notes) NoteEditor.FlushPendingSave();
        _leftPanel = LeftPanel.None;
        RefreshNavIcons();
        AnimateLeftColumn(0, hideOnDone: toHide);
    }

    /// <summary>Tweens the docked column width to <paramref name="toPx"/> with an ease-out curve,
    /// gliding the panel open or closed. Re-entrant: a click mid-slide retargets from the current
    /// width rather than restarting from the edge. <paramref name="hideOnDone"/>, when set, is
    /// collapsed once a close tween lands.</summary>
    private void AnimateLeftColumn(double toPx, FrameworkElement? hideOnDone)
    {
        // Drop any in-flight tween so rapid toggles can't stack Rendering handlers.
        if (_snapAnimHandler != null) CompositionTarget.Rendering -= _snapAnimHandler;

        _snapAnimFrom = SnapshotsColumn.Width.IsAbsolute ? SnapshotsColumn.Width.Value : 0;
        _snapAnimTo = toPx;
        _snapAnimHide = hideOnDone;
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
                if (_snapAnimHide != null) _snapAnimHide.Visibility = Visibility.Collapsed;
            }
        };
        CompositionTarget.Rendering += _snapAnimHandler;
    }

    private void OnSnapshotsChanged()
    {
        // A change while you're looking at the panel is already seen; otherwise it's a new unread.
        if (SnapshotsPanelOpen) { MarkSnapshotsSeen(); PopulateSnapshots(); }
        else RefreshSnapshotsBadge();
    }

    /// <summary>Marks every current snapshot as seen (opening the panel, or a change while it's open),
    /// clearing the rail badge. Persisted so the badge doesn't re-light on relaunch.</summary>
    private void MarkSnapshotsSeen()
    {
        _snapshotsSeenAt = DateTimeOffset.Now;
        SavePanelState();
        RefreshSnapshotsBadge();
    }

    /// <summary>Current text in the snapshots search box; empty means "show everything".</summary>
    private string _snapshotFilter = "";

    /// <summary>Project labels whose group is folded shut. Survives repopulation (search, import,
    /// delete) so a collapse the user made doesn't spring back open on the next keystroke.</summary>
    private readonly HashSet<string> _collapsedSnapshotGroups = new();

    // "Last opened" watermarks — the rail badges show how many snapshots/closed conversations are
    // newer than these, i.e. unread since the last visit. Persisted in panel-state.json.
    private DateTimeOffset? _snapshotsSeenAt;
    private DateTimeOffset? _historySeenAt;

    /// <summary>Writes every panel's fold state, the seen-watermarks, and the Notes panel's open
    /// note to disk (all survive relaunch).</summary>
    private void SavePanelState() => PanelState.Save(new PanelStateShape(
        _collapsedSnapshotGroups.ToList(), _collapsedHistoryGroups.ToList(),
        _snapshotsSeenAt, _historySeenAt,
        _collapsedNoteGroups.ToList(), _lastNotePath, _noteModel,
        AgentCallsigns.Enabled));

    // The group object is kept in sync (not just the set) so that when the ListView recycles a
    // container on scroll, the OneTime IsExpanded x:Bind re-reads the correct, current state.
    private void SnapshotGroup_Expanding(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderExpandingEventArgs args)
    {
        if (sender.Tag is not SnapshotGroup g) return;
        g.IsExpanded = true;
        _collapsedSnapshotGroups.Remove(g.Project);
        SavePanelState();
    }

    private void SnapshotGroup_Collapsed(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderCollapsedEventArgs args)
    {
        if (sender.Tag is not SnapshotGroup g) return;
        g.IsExpanded = false;
        _collapsedSnapshotGroups.Add(g.Project);
        SavePanelState();
    }

    private void SnapshotsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only react to the user typing — not to programmatic Text changes on repopulate.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _snapshotFilter = sender.Text?.Trim() ?? "";
        PopulateSnapshots();
    }

    private static bool Matches(ContextSnapshot s, string q) =>
        s.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.OriginModel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.SummarizerModel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.ProjectLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (s.Recap?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private void PopulateSnapshots()
    {
        var all = _snapshotStore.Items;   // newest-first copy of the shared store
        var storeEmpty = all.Count == 0;

        // The search box only earns its space once there's something to search.
        SnapshotsSearch.Visibility = storeEmpty ? Visibility.Collapsed : Visibility.Visible;

        // Explain the disabled Import buttons when there's a snapshot but no agent to import into.
        SnapshotsNoAgentNotice.IsOpen = !storeEmpty && _sessions.Active == null;

        var q = _snapshotFilter;
        var filtered = string.IsNullOrEmpty(q) ? all : all.Where(s => Matches(s, q)).ToList();

        // Group by project, preserving the store's newest-first order within each group and
        // ordering the groups by their most-recent snapshot (so the freshest project leads).
        // Each group carries its remembered expand/collapse state so folding a project sticks
        // across searches and imports (which both rebuild this list).
        var groups = filtered
            .GroupBy(s => s.ProjectLabel)
            .OrderByDescending(g => g.Max(s => s.CapturedAt))
            .Select(g => new SnapshotGroup(g.Key, g) { IsExpanded = !_collapsedSnapshotGroups.Contains(g.Key) })
            .ToList();

        SnapshotsList.ItemsSource = groups;

        var nothingToShow = groups.Count == 0;
        SnapshotsEmpty.Text = storeEmpty
            ? "No snapshots yet. When you switch a tab's model — or pick Take snapshot from a tab's ⋯ menu — you'll be offered to save the conversation as a snapshot, summarized by a model you choose."
            : $"No snapshots match “{q}”.";
        SnapshotsEmpty.Visibility = nothingToShow ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsScroller.Visibility = nothingToShow ? Visibility.Collapsed : Visibility.Visible;
        RefreshSnapshotsBadge();
    }

    private void RefreshSnapshotsBadge()
    {
        // Unread = snapshots captured after the last visit. Never-visited (null) counts them all.
        var n = _snapshotsSeenAt is { } seen
            ? _snapshotStore.Items.Count(s => s.CapturedAt > seen)
            : _snapshotStore.Count;
        NavSnapshotsBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavSnapshotsBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    /// <summary>Each Import button disables itself when there's no agent to import into — the action
    /// arms an agent's next message, so it's meaningless with none open. Re-evaluated on load, and the
    /// list is repopulated when the agent count crosses zero (so open buttons refresh too).</summary>
    private void SnapshotImport_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) b.IsEnabled = _sessions.Active != null;
    }

    private void SnapshotImport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContextSnapshot snap) return;
        var target = _selected?.View;
        if (target == null) return;   // no agent open — nothing to import into (button is disabled too)

        target.Session.Controller.ImportContext(snap);   // arms the active agent's next message
        SwitchPage("chat");   // so the "context armed" note is visible in the active tab
        if (SnapshotsPanelOpen) CloseLeftPanel();   // get out of the way — the chat is where the confirmation shows
        target.FocusInput();
    }

    private void SnapshotDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContextSnapshot snap) return;
        _snapshotStore.Remove(snap);
        PopulateSnapshots();
    }

    /// <summary>Deletes every snapshot in one project group at once. The group holds exactly what the
    /// panel is SHOWING, so with a search active this deletes only the matches — the prompt says so
    /// rather than claiming "all". One batched store call, so the panel rebuilds once.</summary>
    private async void SnapshotDeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SnapshotGroup group || group.Count == 0) return;

        // Detach before the await: a capture landing mid-dialog repopulates the panel and replaces
        // the group objects. RemoveAll matches on Id, so a stale copy still removes the right rows.
        var targets = group.ToList();
        var project = group.Project;
        var noun = targets.Count == 1 ? "snapshot" : "snapshots";
        var scope = string.IsNullOrEmpty(_snapshotFilter) ? "" : $" matching “{_snapshotFilter}”";

        var dialog = new ContentDialog
        {
            Title = $"Delete {noun}",
            Content = $"Delete {targets.Count} {noun}{scope} in “{project}”? This can't be undone.",
            PrimaryButtonText = $"Delete {targets.Count}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _snapshotStore.RemoveAll(targets);
        PopulateSnapshots();
    }

}
