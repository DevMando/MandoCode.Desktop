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
using Microsoft.UI.Xaml.Controls.Primitives;
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

        // What it was about (first user turn) and where it stopped (last user turn + the agent's
        // last reply).
        var preview = CardPreview.Trim(turns.FirstOrDefault(t => t.R == "u")?.T);
        var closing = CardLinesFor(turns, preview);

        _archive.Add(new SessionArchiveEntry
        {
            Key = key,
            Title = session.Title,
            ProjectRoot = session.ProjectRoot.ProjectRoot,
            Model = session.Controller.ModelName,
            ClosedAt = DateTimeOffset.Now,
            TurnCount = turns.Count,
            Preview = preview,
            LastMessage = closing.LastMessage,
            LastReply = closing.LastReply,
        });
    }

    /// <summary>
    /// The two closing quotes for a conversation: the user's last message and the agent's last reply.
    /// Both are "" — meaning "computed, nothing to show" — rather than null when there's nothing
    /// worth printing, which is what keeps the backfill from retrying the row forever.
    ///
    /// The last user message is suppressed when it IS <paramref name="preview"/>: a single-turn
    /// conversation would otherwise quote the same line twice. The reply has no such clash (it's the
    /// other voice) so it shows even on a one-turn row — that's the case it helps most.
    /// </summary>
    private static SessionArchiveStore.CardLines CardLinesFor(
        IReadOnlyList<ConversationTurn> turns, string? preview)
    {
        var lastSaid = CardPreview.Trim(turns.LastOrDefault(t => t.R == "u")?.T);
        var lastReply = CardPreview.ClipReply(turns.LastOrDefault(t => t.R == "a")?.T);

        return new SessionArchiveStore.CardLines(
            lastSaid == null || lastSaid == preview ? "" : lastSaid,
            lastReply ?? "");
    }

    private void OnArchiveChanged()
    {
        if (HistoryPanelOpen) { MarkHistorySeen(); PopulateHistory(); }
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
        if (HistoryPanelOpen) CloseLeftPanel();
        else OpenHistory();
    }

    private void CloseHistory_Click(object sender, RoutedEventArgs e) => CloseLeftPanel();

    private void OpenHistory()
    {
        MarkHistorySeen();   // opening the panel IS reading it — clear the unread badge
        PopulateHistory();
        ShowLeftPanel(LeftPanel.History);
        _ = BackfillHistoryCardLinesAsync();
    }

    private bool _historyBackfillStarted;

    /// <summary>Fills in <see cref="SessionArchiveEntry.LastMessage"/> and
    /// <see cref="SessionArchiveEntry.LastReply"/> for conversations archived before those were
    /// recorded, so old cards carry the same closing lines as new ones. Deferred to the first History
    /// open rather than startup (it's up to 60 log reads), run off the UI thread, and persisted in one
    /// write. The store's Changed event repopulates the panel.</summary>
    private async Task BackfillHistoryCardLinesAsync()
    {
        if (_historyBackfillStarted) return;
        _historyBackfillStarted = true;

        await Task.Run(() => _archive.BackfillCardLines(entry =>
        {
            var turns = ConversationLog.Load(entry.Key);
            // Recompute the first line the same way too: comparing against the STORED preview would
            // miss a single-turn conversation whose preview was trimmed under an older rule.
            return CardLinesFor(turns, CardPreview.Trim(turns.FirstOrDefault(t => t.R == "u")?.T));
        }));
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

    // ---- full-text search across archived conversations -------------------------
    // The metadata match (title/project/model/preview) is instant and stays synchronous. Conversation
    // BODIES live in per-session log files, so those are scanned off the UI thread behind a debounce
    // and folded in when they land — typing never waits on file IO.

    private readonly ConversationTextCache _historyText = new();
    // Fully qualified: both Microsoft.UI.Dispatching and Windows.System are imported here, and both
    // define DispatcherQueueTimer (same reason MainWindow.Terminal.cs qualifies its timer).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _historySearchDebounce;
    private int _historySearchGeneration;

    /// <summary>Persist-key → snippet, for rows whose CONTENT matched the current query. Replaced
    /// wholesale by each completed scan; never merged, or a stale snippet from a previous query
    /// would be shown against the new one.</summary>
    private Dictionary<string, string> _historyContentHits = new(StringComparer.OrdinalIgnoreCase);

    private void HistorySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _historyFilter = sender.Text?.Trim() ?? "";

        // Show metadata hits straight away; content hits widen the list a moment later.
        _historyContentHits = new(StringComparer.OrdinalIgnoreCase);
        PopulateHistory();
        QueueHistoryContentSearch();
    }

    /// <summary>(Re)arms the debounce. The Tick handler is attached ONCE at creation — re-attaching
    /// per keystroke would stack handlers and fire one scan per character typed.</summary>
    private void QueueHistoryContentSearch()
    {
        if (_historySearchDebounce == null)
        {
            _historySearchDebounce = _dispatcher.CreateTimer();
            _historySearchDebounce.IsRepeating = false;
            _historySearchDebounce.Interval = TimeSpan.FromMilliseconds(220);
            _historySearchDebounce.Tick += (_, _) => _ = RunHistoryContentSearchAsync();
        }

        _historySearchDebounce.Stop();
        if (!ConversationSearch.IsSearchable(_historyFilter)) return;
        _historySearchDebounce.Start();
    }

    /// <summary>Scans every archived conversation's text for the current query on a background
    /// thread. Results are stamped with a generation so a slower earlier scan can't overwrite a
    /// later keystroke's answer.</summary>
    private async Task RunHistoryContentSearchAsync()
    {
        var query = _historyFilter;
        if (!ConversationSearch.IsSearchable(query)) return;

        var generation = ++_historySearchGeneration;
        var keys = _archive.Items.Select(e => e.Key).ToList();
        var cache = _historyText;

        var hits = await Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                if (ConversationSearch.Snippet(cache.TextFor(key), query) is { } snippet)
                    found[key] = snippet;
            return found;
        });

        // Superseded — a newer keystroke started its own scan while this one was reading.
        if (generation != _historySearchGeneration || query != _historyFilter) return;

        _historyContentHits = hits;
        PopulateHistory();
    }

    /// <summary>Metadata match plus a content hit from the latest completed scan. Not static: the
    /// content hits are per-window state.</summary>
    private bool Matches(SessionArchiveEntry s, string q) =>
        s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.ProjectLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (s.Model?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (s.Preview?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || _historyContentHits.ContainsKey(s.Key);

    private void PopulateHistory()
    {
        var all = _archive.Items;   // newest-first copy
        var storeEmpty = all.Count == 0;
        HistorySearch.Visibility = storeEmpty ? Visibility.Collapsed : Visibility.Visible;

        var q = _historyFilter;
        var filtered = string.IsNullOrEmpty(q) ? all : all.Where(s => Matches(s, q)).ToList();

        // Stamp the snippet onto EVERY row, not just the matches, so a snippet from a previous query
        // can't linger on a row the new query matched by title. Read once by a OneTime x:Bind —
        // ItemsSource is reassigned below, so the templates always re-bind.
        foreach (var entry in all)
            entry.MatchSnippet = _historyContentHits.TryGetValue(entry.Key, out var snippet) ? snippet : null;

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
        _historyText.Forget(new[] { entry.Key });        // its searchable text is gone too
        PopulateHistory();
    }

    /// <summary>Deletes every conversation in one project group at once. The group holds exactly what
    /// the panel is SHOWING, so with a search active this deletes only the matches — the prompt says
    /// so rather than claiming "all". One batched store call, so the panel rebuilds once.</summary>
    private async void HistoryDeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryGroup group || group.Count == 0) return;

        // Detach before the await: an agent closing mid-dialog repopulates the panel and replaces
        // the group objects. Keys stay valid, and RemoveAll ignores any that are already gone.
        var keys = group.Select(s => s.Key).ToList();
        var project = group.Project;
        var noun = keys.Count == 1 ? "conversation" : "conversations";
        var scope = string.IsNullOrEmpty(_historyFilter) ? "" : $" matching “{_historyFilter}”";

        var dialog = new ContentDialog
        {
            Title = $"Delete {noun}",
            Content = $"Delete {keys.Count} {noun}{scope} in “{project}”? "
                      + "Their transcripts and memory are removed from disk and this can't be undone.",
            PrimaryButtonText = $"Delete {keys.Count}",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _archive.RemoveAll(keys, deleteFiles: true);
        _historyText.Forget(keys);
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

        // Right-click anywhere on the tab opens the SAME menu instance rather than a second one
        // attached as ContextFlyout — one MenuFlyout can't be parented in two places.
        entry.Header.RightTapped += (_, e) =>
        {
            e.Handled = true;
            menu.ShowAt(entry.Header, new FlyoutShowOptions { Position = e.GetPosition(entry.Header) });
        };

        // Split-view membership: the one item whose meaning depends on state, so its text and
        // enabled-ness are refreshed each time the menu opens. Deferred to the next dispatcher tick
        // like every other split mutation — it restructures the visual tree.
        var splitItem = new MenuFlyoutItem { Icon = new FontIcon { Glyph = "" } };   // split panes
        splitItem.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tabs.Contains(entry)) return;
            if (_splitPanes.Any(p => ReferenceEquals(p, entry))) RemovePane(entry);
            else AddPane(entry);
        });
        menu.Opening += (_, _) =>
        {
            var paned = _splitPanes.Any(p => ReferenceEquals(p, entry));
            splitItem.Text = paned ? "Remove from split view" : "Add to split view";
            // Adding needs another agent to compare against and a free pane slot.
            splitItem.IsEnabled = paned || (_tabs.Count >= 2 && _splitPanes.Count < MaxSplitPanes);
        };

        var rename = new MenuFlyoutItem { Text = "Rename…", Icon = new FontIcon { Glyph = "" } };
        rename.Click += (_, _) => _ = RenameTabAsync(entry);

        var snapshot = new MenuFlyoutItem { Text = "Take snapshot", Icon = new FontIcon { Glyph = "" } };
        snapshot.Click += (_, _) => entry.View.TakeSnapshotManually();

        var export = new MenuFlyoutItem { Text = "Export transcript…", Icon = new FontIcon { Glyph = "" } };
        export.Click += (_, _) => _ = entry.View.ExportTranscriptAsync();

        var close = new MenuFlyoutItem { Text = "Close agent", Icon = new FontIcon { Glyph = "" } };
        close.Click += (_, _) => CloseTab(entry);

        menu.Items.Add(splitItem);
        menu.Items.Add(new MenuFlyoutSeparator());
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

        // The system prompt was baked at session start, so a live conversation learns the new
        // name the way it learns other outside-the-conversation facts; the next fresh session
        // bakes it properly via the Title setter's Config.AgentName stamp.
        entry.View.Session.Controller.NoteWorkspaceEvent(
            $"The user renamed you — your name is now “{name}”.");
    }

    /// <summary>Selecting an agent also returns you to the chat page — the Settings you were
    /// looking at belonged to the agent you just left.</summary>
    private void SelectTab(ChatTabEntry entry)
    {
        // Selecting a tab NEVER changes the pane set — it only changes the active agent. If that
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
        if (SnapshotsPanelOpen) PopulateSnapshots();   // no agent now → disable Import + show notice
        if (NotesPanelOpen) PopulateNotes();           // no agent now → a new note lands unfiled
    }

}
