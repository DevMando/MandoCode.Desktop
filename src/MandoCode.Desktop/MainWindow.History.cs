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
    // History panel — reopen a closed conversation. Shares the docked column with Snapshots.
    // ============================================================

    /// <summary>Files a just-closed tab into the archive so it can be reopened later. A session that
    /// never had a real turn is forgotten instead (deleting its files), same as <c>/clear</c> —
    /// there's nothing worth reopening, and an empty row would only be noise.</summary>
    private void ArchiveClosedSession(AgentSession session)
    {
        var key = session.PersistKey;
        var turns = ConversationLog.Load(key);
        if (turns.Count == 0)
        {
            TranscriptJournal.Delete(key);
            ConversationLog.Delete(key);
            SessionHistoryStore.Delete(key);
            return;
        }

        var preview = turns.FirstOrDefault(t => t.R == "u")?.T?.Trim();
        if (preview is { Length: > 140 }) preview = preview[..140].TrimEnd() + "…";

        _archive.Add(new SessionArchiveEntry
        {
            Key = key,
            Title = session.Title,
            ProjectRoot = session.ProjectRoot.ProjectRoot,
            Model = session.Controller.ModelName,
            ClosedAt = DateTimeOffset.Now,
            TurnCount = turns.Count,
            Preview = preview,
        });
    }

    private void OnArchiveChanged()
    {
        if (_historyPanelOpen) { MarkHistorySeen(); PopulateHistory(); }
        else RefreshHistoryBadge();
    }

    /// <summary>Marks every current archived conversation as seen, clearing the History rail badge.</summary>
    private void MarkHistorySeen()
    {
        _historySeenAt = DateTimeOffset.Now;
        SavePanelState();
        RefreshHistoryBadge();
    }

    private void NavHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPanelOpen) CloseLeftPanel();
        else OpenHistory();
    }

    private void CloseHistory_Click(object sender, RoutedEventArgs e) => CloseLeftPanel();

    private void OpenHistory()
    {
        MarkHistorySeen();   // opening the panel IS reading it — clear the unread badge
        PopulateHistory();
        ShowLeftPanel(HistoryPanel, snapshots: false);
    }

    /// <summary>Current text in the history search box; empty means "show everything".</summary>
    private string _historyFilter = "";

    /// <summary>Project labels whose History group is folded shut (survives search/reopen/delete).</summary>
    private readonly HashSet<string> _collapsedHistoryGroups = new();

    private void HistoryGroup_Expanding(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderExpandingEventArgs args)
    {
        if (sender.Tag is not HistoryGroup g) return;
        g.IsExpanded = true;
        _collapsedHistoryGroups.Remove(g.Project);
        SavePanelState();
    }

    private void HistoryGroup_Collapsed(Microsoft.UI.Xaml.Controls.Expander sender, Microsoft.UI.Xaml.Controls.ExpanderCollapsedEventArgs args)
    {
        if (sender.Tag is not HistoryGroup g) return;
        g.IsExpanded = false;
        _collapsedHistoryGroups.Add(g.Project);
        SavePanelState();
    }

    private void HistorySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _historyFilter = sender.Text?.Trim() ?? "";
        PopulateHistory();
    }

    private static bool Matches(SessionArchiveEntry s, string q) =>
        s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.ProjectLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (s.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (s.Preview?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    private void PopulateHistory()
    {
        var all = _archive.Items;   // newest-first copy
        var storeEmpty = all.Count == 0;
        HistorySearch.Visibility = storeEmpty ? Visibility.Collapsed : Visibility.Visible;

        var q = _historyFilter;
        var filtered = string.IsNullOrEmpty(q) ? all : all.Where(s => Matches(s, q)).ToList();

        // Group by project (freshest project first), newest-first within each, carrying remembered
        // collapse state — same shape as the Snapshots panel.
        var groups = filtered
            .GroupBy(s => s.ProjectLabel)
            .OrderByDescending(g => g.Max(s => s.ClosedAt))
            .Select(g => new HistoryGroup(g.Key, g) { IsExpanded = !_collapsedHistoryGroups.Contains(g.Key) })
            .ToList();

        HistoryList.ItemsSource = groups;

        var nothingToShow = groups.Count == 0;
        HistoryEmpty.Text = storeEmpty
            ? "No past conversations yet. Close a tab and it lands here — reopen it any time to pick up where you left off. (Clearing a tab with /clear forgets it for good; closing keeps it.)"
            : $"No conversations match “{q}”.";
        HistoryEmpty.Visibility = nothingToShow ? Visibility.Visible : Visibility.Collapsed;
        HistoryScroller.Visibility = nothingToShow ? Visibility.Collapsed : Visibility.Visible;
        RefreshHistoryBadge();
    }

    private void RefreshHistoryBadge()
    {
        // Unread = conversations closed after the last visit. Never-visited (null) counts them all.
        var n = _historySeenAt is { } seen
            ? _archive.Items.Count(s => s.ClosedAt > seen)
            : _archive.Count;
        NavHistoryBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavHistoryBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    /// <summary>Reopens an archived conversation as a fresh tab on its original persist-key, so the
    /// standard restore cascade (transcript replay → memory rehydrate) brings it back. The row
    /// leaves the archive — it's live again — but its files stay; closing re-archives it.</summary>
    private void HistoryOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionArchiveEntry entry) return;

        // Defensive: an archived key should never also be open, but if it is, just go there.
        var existing = _tabs.FirstOrDefault(t =>
            string.Equals(t.View.Session.PersistKey, entry.Key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _archive.Remove(entry.Key, deleteFiles: false);
            CloseLeftPanel();
            SwitchPage("chat");
            SelectTab(existing);
            return;
        }

        // Fall back to the current directory if the original folder is gone — the transcript and
        // memory still restore; only new file operations would need a live folder.
        var root = Directory.Exists(entry.ProjectRoot) ? entry.ProjectRoot : Environment.CurrentDirectory;
        var tab = CreateChatTab(root, entry.Title, entry.Model, entry.Key);   // CreateChatTab selects it
        _archive.Remove(entry.Key, deleteFiles: false);
        CloseLeftPanel();
        SwitchPage("chat");
        _ = InitTabAsync(tab);   // InitializeAsync replays the transcript; then model + memory restore
        SaveWorkspace();
    }

    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionArchiveEntry entry) return;
        _archive.Remove(entry.Key, deleteFiles: true);   // explicit forget — files go too
        PopulateHistory();
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
            TextTrimming = TextTrimming.CharacterEllipsis
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

        // A Grid (not a StackPanel) so the label flexes and ellipsizes when the tab is narrow,
        // while the badge and options button stay pinned at the right. LayoutTabStrip sets each
        // header's Width; this just governs how that width is divided.
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(badge, 1);
        Grid.SetColumn(options, 2);
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

        var row = (Grid)entry.Header.Child;
        var options = (Button)row.Children[^1];

        var menu = new MenuFlyout();

        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => _ = RenameTabAsync(entry);

        var snapshot = new MenuFlyoutItem { Text = "Take snapshot", Icon = new FontIcon { Glyph = "" } };
        snapshot.Click += (_, _) => entry.View.TakeSnapshotManually();

        var export = new MenuFlyoutItem { Text = "Export transcript…", Icon = new FontIcon { Glyph = "" } };
        export.Click += (_, _) => _ = entry.View.ExportTranscriptAsync();

        var close = new MenuFlyoutItem { Text = "Close agent", Icon = new FontIcon { Glyph = "" } };
        close.Click += (_, _) => CloseTab(entry);

        menu.Items.Add(rename);
        menu.Items.Add(snapshot);
        menu.Items.Add(export);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(close);

        // Closing the last agent is allowed now — it leaves an empty chat (see EnterEmptyState);
        // Settings/MCP simply disable until a new agent exists.

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
        // Selecting a tab NEVER changes the compare pair — it only changes the active agent. If that
        // agent is in the pair, ApplyPaneLayout shows the split; otherwise it shows the agent single.
        _selected = entry;
        _sessions.Activate(entry.View.Session);
        RefreshTabStrip();
        SwitchPage("chat");

        // Reveal the selected tab. Try now (covers clicking an already-laid-out tab) and again when
        // the strip re-lays-out (covers a just-added agent, whose width/extent settle a frame later,
        // via TabStrip_SizeChanged). Pending stays set until the tab is actually laid out.
        _scrollToSelectedPending = true;
        DispatcherQueue.TryEnqueue(TryScrollToSelected);
    }

    private bool _scrollToSelectedPending;

    // Scroll the strip so the selected tab is fully visible — a manual ChangeView so a newly created
    // (last) tab scrolls ALL THE WAY to the end. StartBringIntoView only did a minimal scroll and ran
    // before the extent settled, so it stopped short. No-op once the tab is visible; stays pending
    // (retried on the next strip SizeChanged) while the tab isn't laid out yet (ActualWidth == 0).
    private void TryScrollToSelected()
    {
        if (!_scrollToSelectedPending || _selected is null) return;
        var header = _selected.Header;
        if (header.ActualWidth <= 0) return;   // not laid out yet — retry on the next SizeChanged

        double left = header.TransformToVisual(TabStrip)
                            .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        double right = left + header.ActualWidth;
        double viewLeft = TabScroller.HorizontalOffset;
        double viewRight = viewLeft + TabScroller.ViewportWidth;
        const double pad = 8;

        if (right > viewRight)                 // off the right (e.g. a just-added last tab)
            TabScroller.ChangeView(right - TabScroller.ViewportWidth + pad, null, null);
        else if (left < viewLeft)              // off the left
            TabScroller.ChangeView(Math.Max(0, left - pad), null, null);

        _scrollToSelectedPending = false;
    }

    private void TabStrip_SizeChanged(object sender, SizeChangedEventArgs e) => TryScrollToSelected();

    private void CloseTab(ChatTabEntry entry)
    {
        var index = _tabs.IndexOf(entry);
        if (index < 0) return;

        _tabs.RemoveAt(index);
        TabStrip.Children.Remove(entry.Header);

        // Shut down BEFORE unparenting. Removing the view from the tree unloads the WebView2 and
        // nulls its CoreWebView2, so Close() and any last transcript write would hit null.
        entry.View.Shutdown();
        TabHost.Children.Remove(entry.View);
        _sessions.CloseSession(entry.View.Session);
        ArchiveClosedSession(entry.View.Session);   // closed tab = recoverable from History, not gone

        // Closing the last agent is allowed: you're left with the empty chat background until you
        // open another. Settings/MCP disable meanwhile (they act on an agent), handled in SwitchPage.
        if (_tabs.Count == 0)
        {
            _selected = null;
            ValidateSplit();     // nothing left to compare → exits split
            EnterEmptyState();
            SaveWorkspace();
            return;
        }

        if (!ReferenceEquals(_selected, entry))
        {
            ValidateSplit();     // repair the right pane if that's what closed
            RefreshTabStrip();
            SaveWorkspace();
            return;
        }

        _selected = null;
        SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        ValidateSplit();         // the new selection might collide with the right pane
        SaveWorkspace();
    }

    /// <summary>Shows the "no agents open" background — the chat area with nothing in it. Bounces off
    /// any full-screen page back to chat (Settings/MCP have no agent to act on now).</summary>
    private void EnterEmptyState()
    {
        RefreshTabStrip();       // empties the toast; disables Settings/MCP via RefreshNavIcons
        SwitchPage("chat");      // reveals the empty-state panel + its background
        if (_snapshotsPanelOpen) PopulateSnapshots();   // no agent now → disable Import + show notice
    }

}
