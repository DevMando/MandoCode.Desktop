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
    // Tabs
    // ============================================================

    /// <summary>One independent agent: its strip header and its chat surface.</summary>
    private sealed class ChatTabEntry
    {
        public required Border Header { get; init; }
        public required TextBlock Label { get; init; }
        public required Ellipse Badge { get; init; }
        public required ChatTabView View { get; init; }

        /// <summary>Model to select once this tab's harness is initialized — set only for
        /// tabs recreated from a saved workspace. Best-effort: unavailable model = default.</summary>
        public string? RestoreModel { get; init; }

        /// <summary>
        /// True from creation until this tab has actually finished settling onto its own model.
        /// While it is set, the workspace persists <see cref="RestoreModel"/> rather than the live
        /// model: a tab whose restore never completed is sitting on the default it was seeded with,
        /// and writing that would discard the user's real choice permanently. Once restore
        /// finishes — whether the model resolved or errored — the live model is the truth.
        /// </summary>
        public bool ModelRestorePending { get; set; }
    }

    private readonly List<ChatTabEntry> _tabs = new();
    private ChatTabEntry? _selected;
    private ChatTabEntry? _pendingApprovalTab;
    private bool _approvalToastDismissed;

    /// <summary>
    /// The agent everything else acts on: Esc, the Settings page, the MCP page. Stays put while
    /// you're looking at Settings — that's what makes "these settings belong to Agent 2" true.
    /// </summary>
    private ChatTabView? ActiveChat => _selected?.View;

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        var entry = CreateChatTab();
        // No restore cascade on a brand-new agent — nothing is going to move its config out from
        // under it, so its settings can persist from the first change. (Restored tabs are armed by
        // InitTabAsync instead, once their saved model has landed.)
        entry.View.Session.ConfigPersistenceArmed = true;
        _ = entry.View.InitializeAsync();
        SaveWorkspace();
    }

    private ChatTabEntry CreateChatTab(string? projectRoot = null, string? title = null, string? restoreModel = null, string? persistKey = null)
    {
        var session = _sessions.CreateSession(projectRoot, persistKey);
        if (!string.IsNullOrWhiteSpace(title)) session.Title = title;
        var view = new ChatTabView(this, session, _html) { Visibility = Visibility.Collapsed };

        // The agent opens its OWN settings pane (ChatTabView.OnSetupNeeded); all this has to do is
        // make sure the chat is the visible page, so the pane isn't opening behind a rail page.
        view.SetupRequested += () => SwitchPage("chat");
        view.McpEditorRequested += name =>
        {
            SwitchPage("mcp");
            OpenMcpEditor(name);
        };
        view.ClipboardCopyRequested += CopyToClipboard;
        view.ExitRequested += Close;
        view.ApprovalStateChanged += _ => RefreshTabStrip();
        view.HeaderChanged += v =>
        {
            var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.View, v));
            if (tab != null) tab.Label.Text = v.Session.Title;
            SaveWorkspace();   // renames, folder switches, and model switches all land here
        };

        TabHost.Children.Add(view);

        var (header, label, badge) = BuildTabHeader(session.Title);
        var entry = new ChatTabEntry
        {
            Header = header, Label = label, Badge = badge, View = view,
            RestoreModel = restoreModel,
            ModelRestorePending = !string.IsNullOrWhiteSpace(restoreModel),
        };
        _tabs.Add(entry);
        TabStrip.Children.Add(header);
        WireHeader(entry);

        SelectTab(entry);
        if (SnapshotsPanelOpen) PopulateSnapshots();   // an agent exists now → re-enable Import
        if (NotesPanelOpen) PopulateNotes();           // a new note would now be filed under this folder
        if (SplitConfigured) RefreshSplitBar();          // offer the new agent in the pane pickers
        return entry;
    }

}
