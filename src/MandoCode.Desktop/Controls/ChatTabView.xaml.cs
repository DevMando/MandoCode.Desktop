using System.Collections.ObjectModel;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

/// <summary>
/// One agent tab's entire chat surface: transcript, input, suggestions, busy/plan state, and its
/// own approval overlay. Bound to exactly one <see cref="AgentSession"/> for its whole life.
///
/// It implements <see cref="IApprovalUi"/> against its OWN overlay. That's what makes concurrent
/// agents safe: the session's WinUiApprovalService points here, not at the window, so an approval
/// raised by this tab's AIService can only ever render in this tab.
///
/// Kept in the visual tree even when its tab isn't selected (MainWindow toggles Visibility rather
/// than swapping content). A detached WebView2 closes its CoreWebView2, and the transcript DOM is
/// the only copy of the conversation.
/// </summary>
public sealed partial class ChatTabView : UserControl, IApprovalUi
{
    private readonly Window _owner;
    private readonly TranscriptHtmlBuilder _html;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private readonly Queue<string> _pendingHtml = new();
    private bool _webViewReady;
    private bool _initialized;
    private bool _shutDown;

    /// <summary>
    /// Safe to run script against this tab's transcript. Closing the tab nulls out
    /// <c>CoreWebView2</c> while the agent may still be unwinding a cancelled turn, so an
    /// in-flight transcript append would otherwise dereference null.
    /// </summary>
    private bool CanScript => !_shutDown && _webViewReady && TranscriptView.CoreWebView2 != null;

    private TaskCompletionSource<string>? _approvalTcs;
    private TaskCompletionSource<string>? _instructionTcs;

    private readonly ObservableCollection<CommandSuggestion> _suggestions = new();

    private enum SuggestMode { None, Command, File, Emoji }
    private SuggestMode _suggestMode = SuggestMode.None;
    private int _tokenStart;   // index of the '@' (File mode) — replaced on accept
    private int _tokenEnd;     // caret position when suggestions were computed

    public AgentSession Session { get; }

    private ChatController _controller => Session.Controller;
    private TranscriptWriter _transcript => Session.Transcript;
    private MandoCode.Services.FileAutocompleteProvider _fileProvider => Session.FileProvider;

    /// <summary>True while this tab's approval overlay OR the plan-approval bar is up and awaiting a choice.</summary>
    public bool IsApprovalOpen => ApprovalOverlay.Visibility == Visibility.Visible
        || PlanApprovalBar.Visibility == Visibility.Visible;

    /// <summary>The pending approval's toast summary — what's waiting (e.g. "Wants to edit
    /// Program.cs"), set when the approval is shown. MainWindow shows it in the cross-tab toast.</summary>
    public string ApprovalHeadline => _approvalSummary;
    private string _approvalSummary = "";

    /// <summary>Set by MainWindow when this tab is selected. Only a background tab badges.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Approval opened or resolved — MainWindow updates the tab badge and toast.</summary>
    public event Action<ChatTabView>? ApprovalStateChanged;

    /// <summary>Status bar data changed (model, tokens, folder, connection) — refresh the tab title.</summary>
    public event Action<ChatTabView>? HeaderChanged;

    /// <summary>The controller asked for the Settings surface (first run, /config, no connection).</summary>
    public event Action? SetupRequested;

    /// <summary>The controller asked for the MCP editor (null = add new, else server name).</summary>
    public event Action<string?>? McpEditorRequested;

    /// <summary>/exit — close the window.</summary>
    public event Action? ExitRequested;

    /// <summary>The UI should put this text on the clipboard (must marshal to the UI thread).</summary>
    public event Action<string>? ClipboardCopyRequested;

    public ChatTabView(Window owner, AgentSession session, TranscriptHtmlBuilder html)
    {
        InitializeComponent();

        _owner = owner;
        Session = session;
        _html = html;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // This tab's approval service renders into this tab's overlay.
        Session.Approvals.Ui = this;
        SuggestionsList.ItemsSource = _suggestions;
        EmojiGrid.ItemsSource = QuickEmojis;
        TranscriptView.DefaultBackgroundColor = ThemeManager.C(ThemeManager.Current.Background);

        // Method groups, not lambdas: Shutdown has to be able to detach them. A closed tab that
        // stays subscribed keeps receiving transcript blocks from its agent's unwinding turn and
        // drives them into a WebView2 that no longer has a CoreWebView2.
        _transcript.BlockAdded += OnTranscriptBlock;
        _transcript.Cleared += OnTranscriptCleared;
        Session.Busy.Changed += OnBusyChanged;

        _controller.StateChanged += OnControllerStateChanged;
        _controller.PlanProgressChanged += OnPlanProgress;
        _controller.SetupNeeded += OnSetupNeeded;
        _controller.McpEditorRequested += OnMcpEditorRequested;
        _controller.ClipboardCopyRequested += OnClipboardCopy;
        _controller.ExitRequested += OnExitRequested;
        _controller.SnapshotOfferChanged += OnSnapshotOfferChanged;

        UpdateHeader();
    }

    // Harness events arrive on background threads; each hop marshals to the UI thread.
    private void OnTranscriptBlock(string html) => OnUi(() => AppendHtml(html));
    private void OnTranscriptCleared() => OnUi(ClearTranscript);
    private void OnBusyChanged(bool busy, string? activity) => OnUi(() => UpdateBusy(busy, activity));
    private void OnControllerStateChanged() => OnUi(UpdateHeader);
    private void OnPlanProgress(int done, int total, bool active) => OnUi(() => UpdatePlanProgress(done, total, active));
    private void OnSetupNeeded() => OnUi(() => SetupRequested?.Invoke());
    private void OnMcpEditorRequested(string? name) => OnUi(() => McpEditorRequested?.Invoke(name));
    private void OnClipboardCopy(string text) => OnUi(() => ClipboardCopyRequested?.Invoke(text));
    private void OnExitRequested() => OnUi(() => ExitRequested?.Invoke());
    private void OnSnapshotOfferChanged() => OnUi(RefreshSnapshotOffer);

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    // ============================================================
    // Lifecycle
    // ============================================================

    /// <summary>Boots the WebView2 and the harness connection. Safe to call more than once.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // A new agent's WebView2 is added to the tree microseconds before this runs. Creating
            // the CoreWebView2 before the control is Loaded leaves it null, so wait for Loaded.
            await WaitForLoadedAsync();
            if (_shutDown) return;

            await TranscriptView.EnsureCoreWebView2Async();

            var core = TranscriptView.CoreWebView2;
            if (core == null)
            {
                ModelText.Text = "WebView2 failed to initialize for this agent.";
                return;
            }

            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;   // F12 in the transcript
            core.NavigationCompleted += async (_, _) =>
            {
                // Replay the journaled transcript FIRST (while _webViewReady is still false,
                // so live blocks keep queueing) — restored history must precede this
                // launch's boot output.
                await RestoreJournaledTranscriptAsync();
                _webViewReady = true;
                while (_pendingHtml.Count > 0) AppendHtml(_pendingHtml.Dequeue());
            };

            // The WebView hosts only the transcript document. Any link click opens in the
            // default browser instead of navigating the transcript away.
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenInBrowser(e.Uri);
                }
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenInBrowser(e.Uri);
            };

            // File-path links in transcript cards post "open-file:<path>" messages
            // (see TranscriptHtmlBuilder.FileLink).
            core.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { /* non-string message — not ours */ }
                if (msg != null && msg.StartsWith("open-file:", StringComparison.Ordinal))
                    OpenTranscriptPath(msg["open-file:".Length..]);
                else if (msg != null && msg.StartsWith("copy:", StringComparison.Ordinal))
                    ClipboardCopyRequested?.Invoke(msg["copy:".Length..]);
                else if (msg != null && msg.StartsWith("react:", StringComparison.Ordinal))
                    HandleReaction(msg["react:".Length..], add: true);
                else if (msg != null && msg.StartsWith("unreact:", StringComparison.Ordinal))
                    HandleReaction(msg["unreact:".Length..], add: false);
                else if (msg == "drag-enter")
                    ShowDropOverlay();   // a drag crossed onto the WebView surface — see DropOverlay
                else if (msg != null && msg.StartsWith("undo-file:", StringComparison.Ordinal))
                    UndoFileFromCard(msg["undo-file:".Length..]);   // interactive diff card's Undo chip
            };

            // Serve bundled web assets (highlight.js) to the transcript document.
            // Missing folder just means syntax highlighting silently doesn't engage.
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    "mandocode.assets",
                    Path.Combine(AppContext.BaseDirectory, "Assets", "web"),
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            // User-data host: serves the chat background image (see ThemeManager.SetChatBackground).
            // Missing folder just means no background renders.
            try
            {
                Directory.CreateDirectory(ThemeManager.UserDataFolder);
                core.SetVirtualHostNameToFolderMapping(
                    "mandocode.userdata",
                    ThemeManager.UserDataFolder,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            core.NavigateToString(TranscriptHtmlBuilder.BaseDocument(ThemeManager.Current));
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing — extremely rare on Win11, but fail visibly.
            ModelText.Text = $"WebView2 init failed: {ex.Message}";
        }

        if (_shutDown) return;
        await Task.Run(_controller.InitializeAsync);
    }

    /// <summary>A reaction chip was toggled in the transcript. Payload is JSON from the
    /// transcript's rxChip handler: { id, emoji, snippet }. Adds/removes the pending entry
    /// the controller folds into the next model turn; malformed payloads are ignored.</summary>
    private void HandleReaction(string json, bool add)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString() ?? "";
            var emoji = doc.RootElement.GetProperty("emoji").GetString() ?? "";
            var snippet = doc.RootElement.GetProperty("snippet").GetString() ?? "";
            if (emoji.Length == 0) return;
            if (add) _controller.AddReaction(id, emoji, snippet);
            else _controller.RemoveReaction(id, emoji);
        }
        catch { /* malformed payload — not ours to crash over */ }
    }

    private Task WaitForLoadedAsync()
    {
        if (TranscriptView.IsLoaded) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            TranscriptView.Loaded -= OnLoaded;
            tcs.TrySetResult();
        }
        TranscriptView.Loaded += OnLoaded;

        // Loaded may have fired between the check and the subscription.
        if (TranscriptView.IsLoaded)
        {
            TranscriptView.Loaded -= OnLoaded;
            tcs.TrySetResult();
        }
        return tcs.Task;
    }

    /// <summary>Recolors this tab's WebView2 transcript in place (CSS variables) on theme change.</summary>
    public async void ApplyTheme()
    {
        if (_shutDown) return;
        var theme = ThemeManager.Current;
        TranscriptView.DefaultBackgroundColor = ThemeManager.C(theme.Background);

        var core = CanScript ? TranscriptView.CoreWebView2 : null;   // captured: see AppendRawAsync
        if (core == null) return;
        try { await core.ExecuteScriptAsync(ThemeManager.BuildTranscriptScript(theme)); }
        catch { /* WebView gone (window closing) — nothing to recolor */ }
    }

    public void FocusInput() => InputBox.Focus(FocusState.Programmatic);

    /// <summary>
    /// Tears this agent down: cancels anything in flight and closes the WebView2.
    ///
    /// Closing the WebView2 is not optional. Its CoreWebView2 owns a set of browser processes
    /// that outlive the control — dropping the reference leaves them running for the rest of the
    /// app's life, so every closed tab would cost a handful of orphaned msedgewebview2 processes.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _webViewReady = false;
        StopExplorerWatcher();

        // Detach BEFORE cancelling. A cancelled turn unwinds through the harness and writes its
        // last blocks to the transcript; still-attached handlers would drive those into a
        // WebView2 whose CoreWebView2 is about to be null.
        _transcript.BlockAdded -= OnTranscriptBlock;
        _transcript.Cleared -= OnTranscriptCleared;
        Session.Busy.Changed -= OnBusyChanged;
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.PlanProgressChanged -= OnPlanProgress;
        _controller.SetupNeeded -= OnSetupNeeded;
        _controller.McpEditorRequested -= OnMcpEditorRequested;
        _controller.ClipboardCopyRequested -= OnClipboardCopy;
        _controller.ExitRequested -= OnExitRequested;
        _controller.SnapshotOfferChanged -= OnSnapshotOfferChanged;

        _controller.CancelActiveRequest();

        // Release any approval this agent was blocking on, or its harness task never unwinds.
        _approvalTcs?.TrySetResult(ApprovalSignals.Cancelled);
        _instructionTcs?.TrySetResult(ApprovalSignals.Cancelled);

        try { TranscriptView.Close(); }
        catch { /* already gone, or WebView2 never initialized */ }
    }

    /// <summary>Esc from anywhere in the window, routed here by MainWindow.</summary>
    public void HandleEscape()
    {
        if (IsApprovalOpen) return;   // the overlay owns Esc while it's up
        _controller.CancelActiveRequest();
    }

}
