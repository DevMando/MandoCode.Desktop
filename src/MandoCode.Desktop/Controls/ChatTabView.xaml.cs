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
        if (!CanScript) return;
        try { await TranscriptView.CoreWebView2.ExecuteScriptAsync(ThemeManager.BuildTranscriptScript(theme)); }
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

    // ============================================================
    // Transcript
    // ============================================================

    private bool _journalRestored;

    /// <summary>Replays this session's journaled transcript into the fresh WebView — via
    /// ExecuteScript directly, NOT through TranscriptWriter (that would re-journal every
    /// block). Chunked so a long history is a few script calls, not a thousand.</summary>
    private async Task RestoreJournaledTranscriptAsync()
    {
        if (_journalRestored) return;
        _journalRestored = true;
        try
        {
            var blocks = TranscriptJournal.Load(Session.PersistKey)
                .Where(b => !TranscriptHtmlBuilder.IsEphemeralStatus(b))
                .ToList();
            if (blocks.Count == 0) return;

            var chunk = new System.Text.StringBuilder();
            foreach (var block in blocks)
            {
                chunk.Append(block);
                if (chunk.Length > 400_000)
                {
                    await AppendRawAsync(chunk.ToString());
                    chunk.Clear();
                }
            }
            if (chunk.Length > 0) await AppendRawAsync(chunk.ToString());

            // Divider goes through AppendRawAsync too — journaling it would stack one
            // divider per relaunch. Memory restore happens LATER (RestoreConversationMemoryAsync,
            // called by MainWindow after any saved model is re-selected — a model switch clears
            // history, so restoring memory here would risk it being wiped moments later).
            _replayedBlockCount = blocks.Count;
            await AppendRawAsync(_html.Dim("— restored from your previous session —"));
        }
        catch { /* a failed replay must never block a fresh conversation */ }
    }

    private int _replayedBlockCount;

    /// <summary>
    /// Gives the restored session its memory back, best fidelity first. Called by MainWindow
    /// AFTER the tab's harness is initialized and any saved model re-selected. Cascade:
    /// 1) full-fidelity harness history (the agent genuinely remembers, tool calls included);
    /// 2) plain-text tail armed as imported background (briefed, not remembering);
    /// 3) an honest amnesia note, so the model never has to guess about the replayed pixels.
    /// Fresh tabs have none of the files and fall straight through as a no-op.
    /// </summary>
    public async Task RestoreConversationMemoryAsync()
    {
        try
        {
            // 1) Full fidelity: rehydrate the harness's ChatHistory verbatim.
            var historyJson = SessionHistoryStore.Load(Session.PersistKey);
            if (historyJson != null)
            {
                var restored = await Task.Run(() => Session.Ai.TryRestoreHistoryJson(historyJson));
                if (restored > 0)
                {
                    await AppendRawAsync(_html.Dim(
                        $"Conversation memory restored — the agent remembers this session ({restored} messages)."));
                    return;
                }
            }

            // 2) Tail-brief: bounded verbatim excerpt rides the next send as imported background.
            var turns = ConversationLog.Load(Session.PersistKey);
            if (turns.Count > 0)
            {
                const int budget = 12_000;
                var picked = new List<ConversationTurn>();
                var used = 0;
                for (var i = turns.Count - 1; i >= 0; i--)
                {
                    if (picked.Count > 0 && used + turns[i].T.Length > budget) break;
                    picked.Add(turns[i]);
                    used += turns[i].T.Length;
                }
                picked.Reverse();

                var sb = new System.Text.StringBuilder();
                if (picked.Count < turns.Count)
                    sb.Append($"(Older turns omitted — this is the most recent {picked.Count} of {turns.Count}.)\n\n");
                foreach (var turn in picked)
                    sb.Append(turn.R == "u" ? "User: " : "Assistant: ").Append(turn.T).Append("\n\n");

                _controller.ArmRestoredConversation(
                    "From \"your previous session in this tab\" (verbatim excerpt, not a recap):\n" +
                    sb.ToString().TrimEnd());
                await AppendRawAsync(_html.Dim(
                    "Context re-armed — the agent will be briefed on this conversation with your next message."));
                return;
            }

            // 3) Transcript was replayed but no memory of any kind exists — say so to the model.
            if (_replayedBlockCount > 0)
                _controller.NoteWorkspaceEvent(
                    "This tab was restored from a previous session. The transcript the user sees above is a replay " +
                    "for their benefit; it is NOT in your context and you have no memory of it. If the user refers " +
                    "to earlier work, say so honestly and re-read files instead of guessing.");
        }
        catch { /* memory restore is best-effort; a fresh conversation always works */ }
    }

    private async Task AppendRawAsync(string html)
    {
        try
        {
            await TranscriptView.CoreWebView2.ExecuteScriptAsync(
                $"window.__append({JsonSerializer.Serialize(html)})");
        }
        catch { }
    }

    private async void AppendHtml(string html)
    {
        if (_shutDown) return;
        if (!CanScript)
        {
            _pendingHtml.Enqueue(html);
            return;
        }

        try
        {
            await TranscriptView.CoreWebView2.ExecuteScriptAsync(
                $"window.__append({JsonSerializer.Serialize(html)})");
        }
        catch
        {
            // A fragment failing to render must never take the app down.
        }
    }

    private async void ClearTranscript()
    {
        if (!CanScript) return;
        try { await TranscriptView.CoreWebView2.ExecuteScriptAsync("window.__clear()"); }
        catch { }
    }

    /// <summary>Offer to snapshot this tab's conversation (the "Take snapshot" tab action) — pops the
    /// opt-in create card so the user can pick a summarizer model.</summary>
    public void TakeSnapshotManually() => _ = _controller.OfferManualSnapshotAsync();

    // ============================================================
    // Create-snapshot offer card — shown when the controller buffers a conversation (on a model
    // switch or "Take snapshot"). The user picks a summarizer model and creates, or dismisses to
    // discard. Snapshots are born summarized; there is no light/un-enhanced state.
    // ============================================================

    /// <summary>Shows or hides the top offer to match the controller's pending buffer. Stage 1 is the
    /// thin notification bar; the full picker is built only when the user clicks Create on it.</summary>
    private void RefreshSnapshotOffer()
    {
        var offer = _controller.PendingOffer;
        if (offer == null)
        {
            SnapshotOfferRoot.Visibility = Visibility.Collapsed;
            return;
        }

        // A manual "Take snapshot" is an explicit decision — skip the notification bar and open the
        // name+model picker straight away. Model switches keep stage 1, since there the user may
        // just want to keep working (or "keep memory") rather than snapshot at all.
        if (offer.IsManual)
        {
            ShowSnapshotPickerCard(offer);
            return;
        }

        // Stage 1: notification bar. Non-blocking — the user can ignore it and keep prompting.
        // "Keep memory" only appears when a switch actually cleared a conversation.
        SnapshotKeepMemoryButton.Visibility = _controller.CanCarryMemory
            ? Visibility.Visible : Visibility.Collapsed;
        SnapshotNotifyText.Text = _controller.CanCarryMemory
            ? $"Keep the {offer.OriginModel} conversation going, or save it as a snapshot?"
            : $"Snapshot available — save the {offer.OriginModel} conversation.";
        SnapshotNotifyBar.Visibility = Visibility.Visible;
        SnapshotOfferCard.Visibility = Visibility.Collapsed;
        SnapshotOfferRoot.Visibility = Visibility.Visible;
        SlideSnapshotOfferIn();
    }

    /// <summary>Stage 2: expand into the full name + model picker. Reached either from the
    /// notification bar's Create (model switches) or directly for a manual "Take snapshot". Hangs at
    /// the top until the user creates or dismisses.</summary>
    private void ShowSnapshotPickerCard(ChatController.PendingSnapshot offer)
    {
        SnapshotOfferSubtitle.Text =
            $"{offer.MessageCount} message{(offer.MessageCount == 1 ? "" : "s")} from {offer.OriginModel} — "
            + "name it (or leave blank and the summarizer will), pick a model, and create.";
        SnapshotCreateButton.Content = "Create";
        SnapshotNameBox.Text = "";   // a fresh offer starts unnamed
        // Reset any leftover busy state from a prior, interrupted attempt.
        SnapshotBusyPanel.Visibility = Visibility.Collapsed;
        SnapshotBusyRing.IsActive = false;
        SnapshotOfferContent.Opacity = 1;
        SnapshotOfferContent.IsHitTestVisible = true;
        SnapshotNotifyBar.Visibility = Visibility.Collapsed;
        SnapshotOfferCard.Visibility = Visibility.Visible;
        SnapshotOfferRoot.Visibility = Visibility.Visible;
        SlideSnapshotOfferIn();
        _ = LoadSnapshotModelsAsync(offer.OriginModel);
    }

    private void SnapshotNotifyCreate_Click(object sender, RoutedEventArgs e)
    {
        var offer = _controller.PendingOffer;
        if (offer != null) ShowSnapshotPickerCard(offer);
    }

    /// <summary>Drops the offer down from the top of the transcript with a short fade.</summary>
    private void SlideSnapshotOfferIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation
        {
            From = -18, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(slide, SnapshotOfferTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, SnapshotOfferRoot);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>Populates the model picker without making the card wait on a network round-trip: the
    /// model that had the conversation is shown selected instantly, then the full installed-model list
    /// (an Ollama /api/tags fetch, slow on cloud setups) streams in behind it for "pick another."</summary>
    private async Task LoadSnapshotModelsAsync(string originModel)
    {
        // Instant: seed with just the current model so the card is usable with zero lag.
        var current = new ModelChoice(originModel, MandoCodeConfig.IsCloudModel(originModel));
        SnapshotModelCombo.ItemsSource = new List<ModelChoice> { current };
        SnapshotModelCombo.SelectedIndex = 0;
        SnapshotModelCombo.IsEnabled = true;
        SnapshotCreateButton.IsEnabled = true;

        // Background: fetch the rest so the dropdown fills in for choosing another model.
        var result = await _controller.LoadAvailableModelsAsync();
        if (!result.Ok || result.Models.Count == 0) return;   // keep the single current entry

        // Guard against a race: if a newer offer/switch swapped models while we were fetching, don't
        // clobber its selection with this stale list.
        if ((SnapshotModelCombo.SelectedItem as ModelChoice)?.Name != originModel) return;

        var choices = result.Models
            .Select(m => new ModelChoice(m, MandoCodeConfig.IsCloudModel(m)))
            .ToList();
        if (!choices.Any(c => string.Equals(c.Name, originModel, StringComparison.OrdinalIgnoreCase)))
            choices.Insert(0, current);   // keep the current model even if the list omits it

        SnapshotModelCombo.ItemsSource = choices;
        SnapshotModelCombo.SelectedItem =
            choices.First(c => string.Equals(c.Name, originModel, StringComparison.OrdinalIgnoreCase));
    }

    private async void SnapshotCreate_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotModelCombo.SelectedItem is not ModelChoice choice)
        {
            _transcript.Append(_html.Warn("Pick a model to summarize with first."));
            return;
        }

        // Summarizing can take a while (especially a cloud model), so show a clear busy state:
        // fade the controls out and spin, with the snapshot's name in the message when it has one.
        var name = SnapshotNameBox.Text?.Trim() ?? "";
        SnapshotBusyText.Text = string.IsNullOrEmpty(name)
            ? "Creating snapshot…"
            : $"Creating “{name}” snapshot…";
        SetSnapshotBusy(true);

        var error = await _controller.CreateSnapshotAsync(choice.Name, name);
        if (error != null)
        {
            SetSnapshotBusy(false);
            _transcript.Append(_html.Warn(error));
        }
        // On success the controller clears the offer → SnapshotOfferChanged → RefreshSnapshotOffer
        // hides the whole thing, and a "Snapshot saved" chip lands in the transcript.
    }

    /// <summary>Toggles the create card's busy state: fades the inputs out (and blocks them) while a
    /// centered spinner + "Creating…" text shows.</summary>
    private void SetSnapshotBusy(bool busy)
    {
        SnapshotBusyRing.IsActive = busy;
        SnapshotBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SnapshotOfferContent.IsHitTestVisible = !busy;

        var fade = new DoubleAnimation
        {
            To = busy ? 0.25 : 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, SnapshotOfferContent);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void SnapshotOfferDismiss_Click(object sender, RoutedEventArgs e)
        => _controller.DismissSnapshotOffer();

    /// <summary>"Keep memory": verbatim continuation on the new model. On success the offer
    /// clears itself (SnapshotOfferChanged → RefreshSnapshotOffer); on failure the bar stays
    /// so Snapshot remains available as the salvage path.</summary>
    private void SnapshotKeepMemory_Click(object sender, RoutedEventArgs e)
        => _controller.TryCarryMemoryAcrossSwitch();

    /// <summary>Saves this tab's transcript as a standalone HTML page. Shared by the header save
    /// button and the tab's options menu.</summary>
    public async Task ExportTranscriptAsync()
    {
        if (!CanScript) return;
        try
        {
            var json = await TranscriptView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = JsonSerializer.Deserialize<string>(json) ?? "";

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
            picker.FileTypeChoices.Add("HTML page", new List<string> { ".html" });
            picker.SuggestedFileName = $"mandocode-transcript-{DateTime.Now:yyyy-MM-dd-HHmm}";
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            await Windows.Storage.FileIO.WriteTextAsync(file, "<!DOCTYPE html>\n" + html);
            _transcript.Append(_html.Success($"Transcript saved to {file.Path}"));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't save transcript: {ex.Message}"));
        }
    }

    /// <summary>Opens a clicked transcript path with its default app (folders open in Explorer).
    /// Relative paths — how operation cards display them — resolve against THIS tab's root.</summary>
    private void OpenTranscriptPath(string raw)
    {
        try
        {
            var path = raw.Trim();
            if (!Path.IsPathRooted(path)) path = Path.Combine(Session.ProjectRoot.ProjectRoot, path);
            path = Path.GetFullPath(path);
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
            else
                _transcript.Append(_html.Warn($"Can't open — no longer exists: {path}"));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* a dead link must not crash the app */ }
    }

    // ============================================================
    // Header / busy / plan progress
    // ============================================================

    public void UpdateHeader()
    {
        ModelText.Text = _controller.ModelName;
        ProjectRootText.Text = _controller.ProjectRootPath;
        ConnectionDot.Fill = new SolidColorBrush(
            _controller.ModelError ? Colors.Orange
            : _controller.IsConnected ? Colors.LimeGreen
            : Colors.Gray);

        var tracker = Session.Tokens;
        TokenText.Text = tracker.TotalSessionTokens > 0
            ? $"{MandoCode.Services.TokenTrackingService.FormatTokenCount(tracker.TotalSessionTokens)} tokens"
            : "";

        var processing = _controller.IsProcessing;
        SendIcon.Glyph = processing ? "" : "";   // stop vs send
        SendLabel.Text = processing ? "Stop" : "Send";
        ModelButton.IsEnabled = !processing;   // no model switch mid-turn

        RefreshBranchChip();

        HeaderChanged?.Invoke(this);
    }

    private void UpdateBusy(bool busy, string? activity)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        if (busy) BusyText.Text = string.IsNullOrWhiteSpace(activity) ? "Working..." : activity;
        else
        {
            // Turn just ended: refresh git state and snapshot it as the baseline for
            // detecting OUTSIDE-the-conversation changes before the next send.
            _wsTracker.MarkCapturePending();
            RefreshBranchChip(force: true);

            // Persist the model's full memory as of this turn (tier-3 full fidelity).
            // Off-thread: serialization of a long history shouldn't touch UI latency.
            var key = Session.PersistKey;
            var ai = Session.Ai;
            _ = Task.Run(() => SessionHistoryStore.Save(key, ai.ExportHistoryJson()));
        }
    }

    // ============================================================
    // Workspace-change notes for the model
    // ============================================================
    // The model only knows what happened inside the conversation. Anything else — the undo
    // button discarding its edits, files changed in another editor, external branch switches
    // — is invisible to it and leaves its picture of the working tree stale. The decision
    // logic lives in WorkspaceDeltaTracker (pure, unit-testable); this class only feeds it:
    // turn end → MarkCapturePending, git refresh → CaptureBaselineIfPending, watcher touch →
    // RecordTouch, send → EmitDelta. Notes queue on the controller (same pattern as reactions).

    private GitBranchInfo? _lastGitInfo;
    private readonly WorkspaceDeltaTracker _wsTracker = new();

    /// <summary>Called at send time: queues notes for whatever changed outside the
    /// conversation since the last turn ended, then re-baselines.</summary>
    private void EmitWorkspaceDelta()
    {
        foreach (var note in _wsTracker.EmitDelta(_lastGitInfo))
            _controller.NoteWorkspaceEvent(note);
    }

    // ============================================================
    // Git status strip
    // ============================================================

    private int _branchRefreshSeq;
    private DateTime _lastBranchRefresh = DateTime.MinValue;
    private string? _lastGitRoot;
    private readonly ObservableCollection<GitChangeItem> _changes = new();

    /// <summary>Fire-and-forget refresh of the bottom status strip AND the explorer's Changes
    /// tab (one git call feeds both). Throttled (UpdateHeader runs on every controller state
    /// change) except when the root changed; sequence-guarded so an older, slower git call
    /// can never overwrite a newer result; any failure just hides the strip.</summary>
    private async void RefreshBranchChip(bool force = false)
    {
        var root = _controller.ProjectRootPath;
        if (root != _lastGitRoot) force = true;   // never show the previous folder's state
        if (!force && (DateTime.UtcNow - _lastBranchRefresh).TotalSeconds < 2) return;
        _lastBranchRefresh = DateTime.UtcNow;
        _lastGitRoot = root;

        var seq = ++_branchRefreshSeq;
        var info = await Task.Run(() => GitQuickStatus.TryGet(root));

        if (_shutDown || seq != _branchRefreshSeq) return;
        _lastGitInfo = info;
        UpdateChangesList(info, root);
        _wsTracker.CaptureBaselineIfPending(info);
        if (info == null)
        {
            StatusStrip.Visibility = Visibility.Collapsed;
            return;
        }

        BranchText.Text = info.Branch
            + (info.Ahead > 0 ? $" ↑{info.Ahead}" : "")
            + (info.Behind > 0 ? $" ↓{info.Behind}" : "");

        // One status light: conflicts trump dirty trumps clean.
        var (dotBrush, state) =
            info.Conflicted ? ("MandoRedBrush", "merge conflicts")
            : info.Dirty ? ("MandoGoldBrush", "uncommitted changes")
            : ("MandoGreenBrush", "clean");
        BranchDot.Fill = Application.Current.Resources[dotBrush] as Brush;

        var foreignRoot = info.RepoRoot.Length > 0 && !string.Equals(
            Path.TrimEndingDirectorySeparator(info.RepoRoot),
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        ToolTipService.SetToolTip(StatusStrip,
            (info.Detached ? "Detached HEAD at commit " + info.Branch : "Git branch: " + info.Branch)
            + " — " + state
            + (info.Ahead > 0 || info.Behind > 0
                ? $" ({info.Ahead} ahead, {info.Behind} behind upstream)" : "")
            // Git found the repo in an ANCESTOR folder — say so, or this reads as a ghost.
            + (foreignRoot ? $"\nRepository root: {info.RepoRoot} (this folder is inside that repository)" : ""));
        StatusStrip.Visibility = Visibility.Visible;
    }

    /// <summary>Rebuilds the Changes tab's rows from a fresh git snapshot (UI thread).</summary>
    private void UpdateChangesList(GitBranchInfo? info, string root)
    {
        if (ChangesList.ItemsSource == null) ChangesList.ItemsSource = _changes;

        // Rebuilding the collection re-realizes every ListView row — a visible flash — so
        // bail when this snapshot is identical to what's already shown (the common case:
        // most refreshes confirm state rather than change it). Badges derive from the same
        // data, so they can't have changed either.
        var incoming = info?.Changes ?? (IReadOnlyList<GitChangeEntry>)Array.Empty<GitChangeEntry>();
        if (incoming.Count == _changes.Count)
        {
            var identical = true;
            for (var i = 0; i < incoming.Count; i++)
            {
                if (incoming[i].RelPath != _changes[i].RelPath || incoming[i].Kind != _changes[i].Kind)
                {
                    identical = false;
                    break;
                }
            }
            if (identical) return;
        }

        _changes.Clear();
        if (info != null)
        {
            foreach (var c in info.Changes)
            {
                var relNative = c.RelPath.Replace('/', Path.DirectorySeparatorChar);
                _changes.Add(new GitChangeItem
                {
                    Kind = c.Kind,
                    KindBrush = BrushForKind(c.Kind),
                    KindLabel = c.Kind switch
                    {
                        "!" => "Merge conflict",
                        "U" => "Untracked (new, not yet added)",
                        "A" => "Added",
                        "D" => "Deleted",
                        "R" => "Renamed",
                        _ => "Modified",
                    },
                    Name = Path.GetFileName(c.RelPath.TrimEnd('/')),
                    Dir = Path.GetDirectoryName(relNative)?.Replace(Path.DirectorySeparatorChar, '/') ?? "",
                    FullPath = Path.Combine(root, relNative),
                    RelPath = c.RelPath,
                    TagTooltip = $"Tag in prompt — inserts @{c.RelPath}",
                });
            }
        }

        ChangesTabButton.Content = _changes.Count > 0 ? $"Changes ({_changes.Count})" : "Changes";
        ChangesEmptyText.Visibility = _changesTabActive && _changes.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CommitButton.IsEnabled = _changes.Count > 0;

        RebuildDirtySets(info);
        RefreshExplorerDirtyFlags();
    }

    // --- dirty badges on the file tree ---
    // A changed file gets a gold dot; every ancestor folder gets one too, so a collapsed
    // folder still signals "something inside changed" (VS Code's badge behavior).

    private readonly HashSet<string> _gitDirtyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gitDirtyDirs = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildDirtySets(GitBranchInfo? info)
    {
        _gitDirtyFiles.Clear();
        _gitDirtyDirs.Clear();
        if (info == null) return;
        foreach (var c in info.Changes)
        {
            var rel = c.RelPath.TrimEnd('/');
            // Untracked directories arrive as one "dir/" entry — that's a dir badge, not a file.
            if (c.RelPath.EndsWith('/')) _gitDirtyDirs.Add(rel);
            else _gitDirtyFiles.Add(rel);
            for (var slash = rel.LastIndexOf('/'); slash > 0; slash = rel.LastIndexOf('/'))
            {
                rel = rel[..slash];
                _gitDirtyDirs.Add(rel);
            }
        }
    }

    /// <summary>Re-flags every REALIZED tree node in place (expansion state survives).
    /// Nodes created later pick their flag up at creation in LoadChildNodes.</summary>
    private void RefreshExplorerDirtyFlags()
    {
        Walk(ExplorerTree.RootNodes);

        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Content is ExplorerItem item) item.Dirty = IsItemDirty(item);
                if (node.Children.Count > 0) Walk(node.Children);
            }
        }
    }

    private bool IsItemDirty(ExplorerItem item) =>
        item.IsDirectory ? _gitDirtyDirs.Contains(item.RelPath) : _gitDirtyFiles.Contains(item.RelPath);

    private static Brush? BrushForKind(string kind) =>
        Application.Current.Resources[kind switch
        {
            "!" or "D" => "MandoRedBrush",
            "A" or "U" => "MandoGreenBrush",
            "R" => "MandoSkyBrush",
            _ => "MandoGoldBrush",
        }] as Brush;

    private void UpdatePlanProgress(int done, int total, bool active)
    {
        PlanProgressPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (total > 0)
        {
            PlanProgressBar.Value = done * 100.0 / total;
            PlanProgressText.Text = $"Plan: step {Math.Min(done + 1, total)} of {total}";
        }
    }

    /// <summary>
    /// Populates the model dropdown each time it opens. The flyout appears immediately showing a
    /// loading spinner; this awaits the model list off the UI thread and swaps in the rows (or an
    /// inline error) when it returns. Tab-local — picking a model repins THIS agent only.
    /// </summary>
    private async void ModelFlyout_Opening(object? sender, object e)
    {
        ModelLoadingPanel.Visibility = Visibility.Visible;
        ModelErrorText.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Collapsed;

        var result = await _controller.LoadAvailableModelsAsync();

        if (!result.Ok)
        {
            ModelErrorText.Text = result.Error;
            ModelLoadingPanel.Visibility = Visibility.Collapsed;
            ModelErrorText.Visibility = Visibility.Visible;
            return;
        }

        var sky = (Brush)Application.Current.Resources["MandoSkyBrush"];
        var dim = (Brush)Application.Current.Resources["MandoDimBrush"];
        var badgeBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        var current = _controller.ModelName;

        var items = result.Models.Select(m =>
        {
            var cloud = MandoCodeConfig.IsCloudModel(m);
            return new ModelItem(m, cloud ? "cloud" : "local", cloud ? sky : dim, badgeBg);
        }).ToList();

        ModelList.ItemsSource = items;
        ModelList.SelectedItem = items.FirstOrDefault(
            i => string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase));

        ModelLoadingPanel.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Visible;
    }

    private async void ModelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        ModelFlyout.Hide();
        if (e.ClickedItem is not ModelItem item) return;
        if (string.Equals(item.Name, _controller.ModelName, StringComparison.OrdinalIgnoreCase)) return;

        await Task.Run(() => _controller.SelectModelAsync(item.Name));
        UpdateHeader();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps must initialize pickers with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _transcript.Append(_html.Info($"Project root changed to: {folder.Path}"));
        _transcript.Append(_html.Dim("Rebuilding the AI session for the new project…"));

        // Retargets THIS tab only — its own ProjectRootAccessor, file cache, and kernel.
        // Other agents keep working in their own folders.
        var session = Session;
        await Task.Run(async () =>
        {
            await session.ChangeProjectRootAsync(folder.Path);
            _transcript.Append(_html.Success("✓ Ready."));
        });
        UpdateHeader();
        if (_explorerOpen) BuildExplorerRoot();   // the open tree must follow the new root
    }

    // ============================================================
    // File explorer panel
    // ============================================================

    private bool _explorerOpen;
    private string? _explorerRoot;   // root the tree was last built for

    private void ExplorerButton_Click(object sender, RoutedEventArgs e) => ToggleExplorer(!_explorerOpen);
    private void ExplorerClose_Click(object sender, RoutedEventArgs e) => ToggleExplorer(false);

    private void ExplorerRefresh_Click(object sender, RoutedEventArgs e)
    {
        BuildExplorerRoot();
        RefreshBranchChip(force: true);   // the Changes tab re-reads too
    }

    // --- Files / Changes tabs ---

    private bool _changesTabActive;

    private void FilesTab_Click(object sender, RoutedEventArgs e) => SetExplorerTab(changes: false);
    private void ChangesTab_Click(object sender, RoutedEventArgs e) => SetExplorerTab(changes: true);

    private void SetExplorerTab(bool changes)
    {
        _changesTabActive = changes;
        ExplorerTree.Visibility = changes ? Visibility.Collapsed : Visibility.Visible;
        ChangesList.Visibility = changes ? Visibility.Visible : Visibility.Collapsed;
        ChangesEmptyText.Visibility = changes && _changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesFooter.Visibility = changes ? Visibility.Visible : Visibility.Collapsed;
        CommitButton.IsEnabled = _changes.Count > 0;
        FilesTabButton.FontWeight = changes ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        ChangesTabButton.FontWeight = changes ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        FilesTabButton.Opacity = changes ? 0.55 : 1;
        ChangesTabButton.Opacity = changes ? 1 : 0.55;
    }

    private void ChatRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_explorerOpen) SizeExplorer();
    }

    private void SizeExplorer()
    {
        // Default ~20% of the window, clamped so the tree stays usable on small windows and
        // doesn't waste half a 4K monitor on the other end. Once the user has dragged the
        // splitter, their width wins (re-clamped so a shrunken window can't strand the panel).
        var w = ChatRoot.ActualWidth;
        if (w <= 0) return;
        var target = _explorerUserWidth ?? Math.Clamp(w * 0.20, 220, 460);
        ExplorerPanel.Width = Math.Clamp(target, MinExplorerWidth, MaxExplorerWidth());
    }

    private const double MinExplorerWidth = 180;
    private double MaxExplorerWidth() => Math.Max(MinExplorerWidth, ChatRoot.ActualWidth * 0.6);

    // --- splitter drag (same pointer-capture pattern as MainWindow's terminal splitter) ---

    private double? _explorerUserWidth;   // set on first drag; SizeExplorer defers to it
    private bool _draggingExplorer;
    private double _explorerDragStartWidth;
    private double _explorerDragStartX;

    private void ExplorerSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingExplorer = true;
        _explorerDragStartWidth = ExplorerPanel.ActualWidth;
        _explorerDragStartX = e.GetCurrentPoint(ChatRoot).Position.X;   // stable frame while the grip moves
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void ExplorerSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingExplorer) return;
        // Dragging left grows the panel; right shrinks it.
        var delta = e.GetCurrentPoint(ChatRoot).Position.X - _explorerDragStartX;
        var next = Math.Clamp(_explorerDragStartWidth - delta, MinExplorerWidth, MaxExplorerWidth());
        ExplorerPanel.Width = next;
        _explorerUserWidth = next;
    }

    private void ExplorerSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingExplorer) return;
        _draggingExplorer = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void ToggleExplorer(bool open)
    {
        if (open == _explorerOpen) return;
        _explorerOpen = open;

        // Docked, not overlaid: the panel sits in the transcript row's second column, so
        // showing it RESIZES the transcript (text stays fully readable) and collapsing it
        // gives the width back. No slide animation — animating a WebView2's width forces
        // continuous relayout of the browser surface, and instant dock/undock is how
        // solution-explorer-style panels behave anyway.
        if (open)
        {
            SizeExplorer();
            // (Re)build on open when the tab's root changed since the tree was built — the
            // panel keeps its expansion state across close/open within the same root.
            if (_explorerRoot != _controller.ProjectRootPath) BuildExplorerRoot();
            ExplorerPanel.Visibility = Visibility.Visible;
            ExplorerSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerPanel.Visibility = Visibility.Collapsed;
            ExplorerSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildExplorerRoot()
    {
        _explorerRoot = _controller.ProjectRootPath;
        ExplorerRootText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_explorerRoot));
        ToolTipService.SetToolTip(ExplorerRootText, _explorerRoot);
        ExplorerTree.RootNodes.Clear();
        foreach (var node in LoadChildNodes(_explorerRoot)) ExplorerTree.RootNodes.Add(node);
        StartExplorerWatcher(_explorerRoot);
    }

    // --- filesystem watcher: the tree follows external creates/deletes/renames on its own ---
    // Efficiency comes from three choices: (1) only NAME notifications — content writes don't
    // change tree shape; (2) events debounce into one flush, so a build touching 500 files
    // costs one pass; (3) a flush re-syncs only REALIZED directory nodes — churn under a
    // never-expanded folder (node_modules, bin/obj) is a hash lookup and a skip, because
    // lazy loading will read the truth from disk whenever it's finally expanded.

    private FileSystemWatcher? _fsWatcher;
    private readonly object _fsLock = new();
    private readonly HashSet<string> _pendingFsDirs = new(StringComparer.OrdinalIgnoreCase);
    private bool _fsFlushQueued;
    private bool _fsSyncAll;   // watcher buffer overflowed — re-sync every realized dir

    private void StartExplorerWatcher(string root)
    {
        StopExplorerWatcher();
        try
        {
            _fsWatcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                // LastWrite so EDITS refresh git state (M rows, badges, dirty dot) — name
                // events alone only cover tree shape. Content writes are routed git-only
                // below: they can't change the tree, so they never trigger tree syncs.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,   // max — fewer overflows during big builds
            };
            _fsWatcher.Created += (_, e) => QueueFsEvent(e.FullPath);
            _fsWatcher.Deleted += (_, e) => QueueFsEvent(e.FullPath);
            _fsWatcher.Renamed += (_, e) => { QueueFsEvent(e.OldFullPath); QueueFsEvent(e.FullPath); };
            _fsWatcher.Changed += (_, e) => QueueFsEvent(e.FullPath, treeRelevant: false);
            _fsWatcher.Error += (_, _) => { lock (_fsLock) { _fsSyncAll = true; } QueueFsEvent(root); };
            _fsWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            _fsWatcher = null;   // best-effort — the refresh button still exists
        }
    }

    private void StopExplorerWatcher()
    {
        try { _fsWatcher?.Dispose(); } catch { }
        _fsWatcher = null;
    }

    /// <summary>Threadpool-side: coalesce this event's parent directory into the pending set
    /// and arm one debounced flush. .git churn and content-only writes skip the tree but
    /// still refresh git state — that's how external edits, branch switches, and commits
    /// show up without a manual refresh.</summary>
    private void QueueFsEvent(string fullPath, bool treeRelevant = true)
    {
        bool arm;
        lock (_fsLock)
        {
            var rel = ToRelOrNull(fullPath)?.Replace('\\', '/');
            if (rel == null) return;
            var isGit = rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase);

            // Our OWN git calls write .git/index (+ transient *.lock files) — reacting to
            // those would refresh forever: refresh → git status → index event → refresh…
            // Ignore them; real external actions (checkout, commit) also touch HEAD/refs,
            // which still get through and trigger the refresh we want.
            if (isGit && (rel.EndsWith("/index", StringComparison.OrdinalIgnoreCase)
                       || rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
                return;

            if (!isGit && treeRelevant)
                _pendingFsDirs.Add(Path.GetDirectoryName(fullPath) ?? "");

            // Workspace notes: remember WHICH files were touched while the agent was idle.
            // Status-snapshot diffing alone misses content edits to files that were ALREADY
            // dirty/untracked (their status entry doesn't change) — this set fills that gap.
            // Idle-gated so the agent's own writes never count as external.
            if (!isGit && !_controller.IsProcessing)
                _wsTracker.RecordTouch(rel);

            arm = !_fsFlushQueued;
            _fsFlushQueued = true;
        }
        if (arm) _ = FlushFsEventsAsync();

        string? ToRelOrNull(string p)
        {
            var root = _explorerRoot;
            if (root == null) return null;
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? p[prefix.Length..] : null;
        }
    }

    private async Task FlushFsEventsAsync()
    {
        await Task.Delay(800);   // coalesce the burst
        List<string> dirs;
        bool syncAll;
        lock (_fsLock)
        {
            syncAll = _fsSyncAll;
            _fsSyncAll = false;
            dirs = _pendingFsDirs.ToList();
            _pendingFsDirs.Clear();
            _fsFlushQueued = false;
        }
        OnUi(() =>
        {
            if (_shutDown) return;
            if (syncAll) SyncAllRealizedDirs();
            else foreach (var dir in dirs) SyncRealizedDir(dir);
            RefreshBranchChip(force: true);   // badges, Changes tab, and status strip follow
        });
    }

    /// <summary>Re-syncs one directory's children IF that directory is realized in the tree;
    /// unexpanded directories are skipped (lazy load reads fresh from disk anyway).</summary>
    private void SyncRealizedDir(string dir)
    {
        var list = FindRealizedChildList(dir);
        if (list != null) SyncDirectoryNode(list, dir);
    }

    private void SyncAllRealizedDirs()
    {
        var root = _explorerRoot;
        if (root == null) return;
        SyncDirectoryNode(ExplorerTree.RootNodes, root);
        Walk(ExplorerTree.RootNodes);

        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is { HasUnrealizedChildren: false, Content: ExplorerItem { IsDirectory: true } item })
                {
                    SyncDirectoryNode(n.Children, item.FullPath);
                    Walk(n.Children);
                }
            }
        }
    }

    private IList<TreeViewNode>? FindRealizedChildList(string dir)
    {
        var root = _explorerRoot;
        if (root == null) return null;
        if (PathsEqual(dir, root)) return ExplorerTree.RootNodes;
        return Find(ExplorerTree.RootNodes);

        IList<TreeViewNode>? Find(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Content is ExplorerItem { IsDirectory: true } item && PathsEqual(item.FullPath, dir))
                    return n.HasUnrealizedChildren ? null : n.Children;
                if (n.Children.Count > 0)
                {
                    var found = Find(n.Children);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static bool PathsEqual(string a, string b) => string.Equals(
            Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Minimal diff of a realized directory node against disk: remove rows whose
    /// path vanished, insert new rows at their sorted position. Never rebuilds surviving
    /// nodes, so expansion state below them is preserved.</summary>
    private void SyncDirectoryNode(IList<TreeViewNode> children, string dir)
    {
        var root = _explorerRoot ?? _controller.ProjectRootPath;
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception) { return; }
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var desired = new List<(string Path, bool IsDir)>(dirs.Length + files.Length);
        foreach (var d in dirs) desired.Add((d, true));
        foreach (var f in files) desired.Add((f, false));
        var desiredSet = new HashSet<string>(desired.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);

        for (var i = children.Count - 1; i >= 0; i--)
            if (children[i].Content is ExplorerItem it && !desiredSet.Contains(it.FullPath))
                children.RemoveAt(i);

        var existing = new HashSet<string>(
            children.Select(n => (n.Content as ExplorerItem)?.FullPath ?? ""),
            StringComparer.OrdinalIgnoreCase);

        for (var idx = 0; idx < desired.Count; idx++)
        {
            var (path, isDir) = desired[idx];
            if (existing.Contains(path)) continue;
            var item = isDir ? ExplorerItem.ForFolder(path, root) : ExplorerItem.ForFile(path, root);
            item.Dirty = IsItemDirty(item);
            var node = new TreeViewNode { Content = item };
            if (isDir) node.HasUnrealizedChildren = true;
            children.Insert(Math.Min(idx, children.Count), node);
        }
    }

    /// <summary>One directory level, folders first then files, both alphabetical. Unreadable
    /// or vanished directories render as empty rather than throwing.</summary>
    private List<TreeViewNode> LoadChildNodes(string dir)
    {
        var root = _explorerRoot ?? _controller.ProjectRootPath;
        var nodes = new List<TreeViewNode>();
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception) { return nodes; }
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            var item = ExplorerItem.ForFolder(d, root);
            item.Dirty = IsItemDirty(item);
            nodes.Add(new TreeViewNode { Content = item, HasUnrealizedChildren = true });
        }
        foreach (var f in files)
        {
            var item = ExplorerItem.ForFile(f, root);
            item.Dirty = IsItemDirty(item);
            nodes.Add(new TreeViewNode { Content = item });
        }
        return nodes;
    }

    /// <summary>The row's @ button — shared by the file tree (TreeViewNode rows) and the
    /// Changes list (GitChangeItem rows): tags the file/folder in the prompt, identical
    /// result to dragging the row onto the input box.</summary>
    private void ExplorerTag_Click(object sender, RoutedEventArgs e)
    {
        var ctx = (sender as FrameworkElement)?.DataContext;
        var path = ctx switch
        {
            TreeViewNode { Content: ExplorerItem item } => item.FullPath,
            GitChangeItem change => change.FullPath,
            _ => null,
        };
        if (path != null) InsertFileTokens(new[] { path });
    }

    private void ChangesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<GitChangeItem>().Select(c => c.FullPath).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        e.Data.SetText(string.Join("\n", paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    /// <summary>The row's ± button: show this file's diff as a transcript DiffCard. An
    /// explicit button (not row click) so selecting or starting a drag never spawns a card,
    /// and no click-vs-double-click disambiguation delay is needed.</summary>
    private async void ChangesDiff_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GitChangeItem item || _shutDown) return;

        var root = _controller.ProjectRootPath;
        var diff = await Task.Run(() => GitQuickStatus.TryGetDiff(root, item.RelPath, untracked: item.Kind == "U"));
        if (_shutDown) return;

        if (diff == null)
            _transcript.Append(_html.Warn($"Couldn't get a diff for {item.RelPath}"));
        else if (diff.Lines.Count == 0)
            _transcript.Append(_html.Dim($"{item.RelPath}: {diff.Summary}"));
        else
            _transcript.Append(_html.DiffCard(item.RelPath, diff.Lines, diff.Summary, interactive: true));
    }

    /// <summary>Pre-fills the prompt with a commit request — never sends, never commits.
    /// Caret-aware insert, so tagging files first then clicking Commit… composes naturally
    /// ("@a.cs @b.cs Commit the current changes…"). The user can edit, then sends; the
    /// bottom-bar approval gates the actual git command.</summary>
    private void Commit_Click(object sender, RoutedEventArgs e) =>
        InsertAtCaret("Commit the current changes with an appropriate message");

    private void ChangeUndo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GitChangeItem item)
            UndoFileFromCard(item.RelPath);
    }

    /// <summary>Fire-and-forget bridge for non-async call sites (web message handler, row
    /// button). async void is safe here: ConfirmAndUndoAsync catches nothing fatal — git
    /// failure is reported to the transcript, not thrown.</summary>
    private async void UndoFileFromCard(string relPath) => await ConfirmAndUndoAsync(relPath);

    /// <summary>The one destructive action in the app, so it always confirms first —
    /// whether it came from a Changes row or a diff card's Undo chip.</summary>
    private async Task ConfirmAndUndoAsync(string relPath)
    {
        var dialog = new ContentDialog
        {
            Title = "Discard changes?",
            Content = $"{relPath} will be restored to its state at the last commit. This can't be undone.",
            PrimaryButtonText = "Discard changes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var root = _controller.ProjectRootPath;
        var ok = await Task.Run(() => GitQuickStatus.TryUndoChanges(root, relPath));
        if (_shutDown) return;
        _transcript.Append(ok
            ? _html.Success($"Restored {relPath} to its state at the last commit.")
            : _html.Warn($"Couldn't restore {relPath} — is it still tracked by git?"));
        if (ok)
        {
            // Tell the model explicitly — discarding its work is feedback, not just a file
            // event — and re-baseline so the generic delta doesn't report it a second time.
            _controller.NoteWorkspaceEvent(
                $"The user DISCARDED all uncommitted changes to {relPath} (restored to the last commit). " +
                "If you changed that file earlier, those changes are gone by the user's choice — don't re-apply them unless asked.");
            _wsTracker.MarkCapturePending();
        }
        RefreshBranchChip(force: true);
    }

    private void ChangesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not GitChangeItem item) return;
        if (!File.Exists(item.FullPath)) return;   // deleted entries have nothing to open
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
        }
    }

    private void ExplorerTag_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ((UIElement)sender).Opacity = 1;

    private void ExplorerTag_PointerExited(object sender, PointerRoutedEventArgs e)
        => ((UIElement)sender).Opacity = 0.45;

    private void ExplorerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!args.Node.HasUnrealizedChildren) return;
        args.Node.HasUnrealizedChildren = false;
        if (args.Node.Content is not ExplorerItem item || !item.IsDirectory) return;
        foreach (var child in LoadChildNodes(item.FullPath)) args.Node.Children.Add(child);
    }

    private void ExplorerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // Single click: folders toggle, files only select. Opening is double-click territory
        // (ExplorerTree_DoubleTapped) — a stray single click must never launch an app.
        if (args.InvokedItem is TreeViewNode { Content: ExplorerItem { IsDirectory: true } } node)
            node.IsExpanded = !node.IsExpanded;
    }

    private void ExplorerTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // The template's elements inherit the row's TreeViewNode as DataContext.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TreeViewNode node ||
            node.Content is not ExplorerItem { IsDirectory: false } item)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
        }
    }

    // ============================================================
    // Drag & drop @-references
    // ============================================================

    /// <summary>Dragging explorer rows carries their full paths as text — the input box's
    /// Drop handler recognizes existing paths and converts them to @tokens.</summary>
    private void ExplorerTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var paths = args.Items.OfType<TreeViewNode>()
            .Select(n => n.Content).OfType<ExplorerItem>()
            .Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { args.Cancel = true; return; }
        args.Data.SetText(string.Join("\n", paths));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void InputBox_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    // --- drop-to-tag overlay choreography ---
    // Show when a drag enters the tab: over XAML chrome that's ChatRoot's DragEnter; over the
    // WebView it's the transcript script's 'drag-enter' message (Chromium owns drags there).
    // Hide when the drag leaves the overlay/tab or when any drop completes. Moving between
    // those regions can flicker the overlay off/on for a frame — harmless.

    private void ShowDropOverlay() => DropOverlay.Visibility = Visibility.Visible;
    private void HideDropOverlay() => DropOverlay.Visibility = Visibility.Collapsed;

    private void ChatRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
            ShowDropOverlay();
    }

    private void ChatRoot_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();
    private void DropOverlay_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();

    private void DropOverlay_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void DropOverlay_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        await HandleDropAsync(e);
    }

    private async void InputBox_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        await HandleDropAsync(e);
    }

    /// <summary>Shared drop handling for the input box and the drop-to-tag overlay: paths
    /// become @tokens, ordinary text inserts as text.</summary>
    private async Task HandleDropAsync(DragEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                // Shell drop (Windows Explorer): real files/folders with paths.
                var items = await e.DataView.GetStorageItemsAsync();
                InsertFileTokens(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                // Text drop: explorer-tree rows arrive as newline-joined full paths. If every
                // line is an existing path, tokenize; otherwise it's ordinary dragged text.
                var text = await e.DataView.GetTextAsync();
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 0 && lines.All(l => File.Exists(l) || Directory.Exists(l)))
                    InsertFileTokens(lines);
                else
                    InsertAtCaret(text);
            }
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't read the dropped item: {ex.Message}"));
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Converts full paths into the same @tokens the autocomplete inserts: project-root
    /// relative, forward slashes, trailing '/' for folders. Items outside this tab's project
    /// root can't be resolved by the @ pipeline, so they're skipped with a warning.</summary>
    private void InsertFileTokens(IEnumerable<string> fullPaths)
    {
        var root = _controller.ProjectRootPath;
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var tokens = new List<string>();
        var outside = new List<string>();

        foreach (var raw in fullPaths)
        {
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                outside.Add(full);
                continue;
            }
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            tokens.Add("@" + rel + (Directory.Exists(full) ? "/" : ""));
        }

        if (tokens.Count > 0)
            InsertAtCaret(string.Join(" ", tokens) + " ");
        if (outside.Count > 0)
            _transcript.Append(_html.Warn(
                $"Skipped {outside.Count} dropped item{(outside.Count == 1 ? "" : "s")} outside this tab's project folder — @ references only work under {root}"));
    }

    /// <summary>Inserts at the caret with token-safe spacing: a separating space is added when
    /// the caret touches non-whitespace, so a dropped @token never glues onto existing text.</summary>
    private void InsertAtCaret(string insert)
    {
        var text = InputBox.Text;
        var caret = Math.Clamp(InputBox.SelectionStart, 0, text.Length);
        if (caret > 0 && !char.IsWhiteSpace(text[caret - 1])) insert = " " + insert;
        InputBox.Text = text[..caret] + insert + text[caret..];
        InputBox.SelectionStart = caret + insert.Length;
        InputBox.Focus(FocusState.Programmatic);
    }

    // ============================================================
    // Input handling
    // ============================================================

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.IsProcessing)
        {
            _controller.CancelActiveRequest();
            return;
        }
        SubmitCurrentInput();
    }

    private void SubmitCurrentInput()
    {
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text) || _controller.IsProcessing) return;

        EmitWorkspaceDelta();   // queue outside-the-conversation changes before this send
        InputBox.Text = "";
        HideSuggestions();
        UpdateHeader();

        _ = Task.Run(async () =>
        {
            try
            {
                await _controller.SubmitAsync(text);
            }
            catch (Exception ex)
            {
                _transcript.Append(_html.Error($"Unexpected error: {ex.Message}"));
            }
        });
    }

    // PreviewKeyDown, NOT KeyDown: the TextBox's own class handler runs before instance
    // KeyDown handlers, so with AcceptsReturn=true an Enter had already inserted a newline
    // — which made TextChanged hide the suggestions popup, and the handler then fell
    // through to submit. Preview (tunneling) fires first, so Handled=true genuinely
    // suppresses the newline and Enter-to-accept behaves exactly like a mouse click.
    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (!shift)
            {
                e.Handled = true;

                // If suggestions are open, Enter accepts (falling back to the first row —
                // never submit the half-typed token as a message).
                if (SuggestionsPanel.Visibility == Visibility.Visible)
                {
                    var pick = SuggestionsList.SelectedItem as CommandSuggestion ?? _suggestions.FirstOrDefault();
                    if (pick != null)
                    {
                        AcceptSuggestion(pick);
                        return;
                    }
                }
                SubmitCurrentInput();
            }
        }
        else if (e.Key == VirtualKey.Tab && SuggestionsPanel.Visibility == Visibility.Visible)
        {
            var pick = (SuggestionsList.SelectedItem ?? _suggestions.FirstOrDefault()) as CommandSuggestion;
            if (pick != null)
            {
                e.Handled = true;
                AcceptSuggestion(pick);
            }
        }
        else if (e.Key == VirtualKey.Down && SuggestionsPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            SuggestionsList.SelectedIndex = Math.Min(SuggestionsList.SelectedIndex + 1, _suggestions.Count - 1);
            SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
        }
        else if (e.Key == VirtualKey.Up && SuggestionsPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            SuggestionsList.SelectedIndex = Math.Max(SuggestionsList.SelectedIndex - 1, 0);
            SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (SuggestionsPanel.Visibility == Visibility.Visible) HideSuggestions();
            else _controller.CancelActiveRequest();
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSuggestions();

    private void UpdateSuggestions()
    {
        var text = InputBox.Text;
        var caret = InputBox.SelectionStart;

        // Slash commands: input starts with '/' and is still a single token.
        if (text.StartsWith('/') && !text.Contains(' '))
        {
            var matches = _controller.GetCommandSuggestions(text);
            if (ShowSuggestions(SuggestMode.Command, 0, caret,
                    matches.Select(m => new CommandSuggestion { Command = m.Command, Description = m.Description })))
                return;
        }

        // @file references: find the token containing the caret; if it starts with '@',
        // filter project files/directories through the same provider the CLI uses
        // (directories come back with a trailing '/' — selecting one drills into it).
        var tokenStart = caret;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
            tokenStart--;

        if (tokenStart < caret && tokenStart < text.Length && text[tokenStart] == '@')
        {
            var fragment = text[(tokenStart + 1)..caret];
            List<string> matches;
            try { matches = _fileProvider.FilterFiles(fragment); }
            catch { matches = new List<string>(); }

            if (ShowSuggestions(SuggestMode.File, tokenStart, caret,
                    matches.Select(m => new CommandSuggestion
                    {
                        Command = m,
                        Description = m.EndsWith('/') ? "folder — select to drill in" : "file"
                    })))
                return;
        }

        // :emoji: shortcodes (Slack-style). Two behaviors on the token containing the caret:
        //  - ":name:" fully typed with an exact match → replace it with the emoji right here.
        //  - ":fra" partially typed (2+ chars, no closing ':') → suggest matching shortcodes.
        // The 2-char minimum keeps ordinary colons (":)", "note:") from popping the list.
        if (tokenStart < caret && tokenStart < text.Length && text[tokenStart] == ':')
        {
            var body = text[(tokenStart + 1)..caret];
            if (body.Length > 1 && body.EndsWith(':'))
            {
                var name = body[..^1].ToLowerInvariant();
                var exact = EmojiShortcodes.FirstOrDefault(s => s.Name == name).Emoji;
                if (exact != null)
                {
                    InputBox.Text = text[..tokenStart] + exact + text[caret..];
                    InputBox.SelectionStart = tokenStart + exact.Length;
                    HideSuggestions();
                    return;
                }
            }
            else if (body.Length >= 2 && !body.Contains(':'))
            {
                var frag = body.ToLowerInvariant();
                var matches = EmojiShortcodes.Where(s => s.Name.StartsWith(frag))
                    .Concat(EmojiShortcodes.Where(s => !s.Name.StartsWith(frag) && s.Name.Contains(frag)));

                if (ShowSuggestions(SuggestMode.Emoji, tokenStart, caret,
                        matches.Select(m => new CommandSuggestion
                        {
                            Command = ":" + m.Name + ":",
                            Description = m.Emoji,
                            InsertText = m.Emoji,
                        })))
                    return;
            }
        }

        HideSuggestions();
    }

    private bool ShowSuggestions(SuggestMode mode, int tokenStart, int tokenEnd, IEnumerable<CommandSuggestion> items)
    {
        _suggestions.Clear();
        foreach (var item in items) _suggestions.Add(item);
        if (_suggestions.Count == 0) return false;

        _suggestMode = mode;
        _tokenStart = tokenStart;
        _tokenEnd = tokenEnd;
        SuggestionsPanel.Visibility = Visibility.Visible;
        SuggestionsList.SelectedIndex = 0;
        SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
        return true;
    }

    private void SuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandSuggestion s) AcceptSuggestion(s);
    }

    private void AcceptSuggestion(CommandSuggestion s)
    {
        if (_suggestMode == SuggestMode.File)
        {
            var text = InputBox.Text;
            var start = Math.Min(_tokenStart, text.Length);
            var end = Math.Min(_tokenEnd, text.Length);

            // Replace the @token with the picked path. Directories keep the caret hot
            // (no trailing space) so the reopened popup shows their contents; files
            // close the token with a space.
            var isFolder = s.Command.EndsWith('/');
            var replacement = "@" + s.Command + (isFolder ? "" : " ");
            InputBox.Text = text[..start] + replacement + text[end..];
            InputBox.SelectionStart = start + replacement.Length;

            // Setting .Text resets the caret to 0 BEFORE the line above restores it, and
            // TextChanged runs in that window — it sees no token at caret 0 and hides the
            // popup. Recompute now that the caret is where the user expects it:
            // folder → drilled listing reopens; file → token ended with a space, stays hidden.
            UpdateSuggestions();
        }
        else if (_suggestMode == SuggestMode.Emoji)
        {
            var text = InputBox.Text;
            var start = Math.Min(_tokenStart, text.Length);
            var end = Math.Min(_tokenEnd, text.Length);
            var emoji = s.InsertText ?? s.Command;
            InputBox.Text = text[..start] + emoji + text[end..];
            InputBox.SelectionStart = start + emoji.Length;
            HideSuggestions();
        }
        else
        {
            InputBox.Text = s.Command + " ";
            InputBox.SelectionStart = InputBox.Text.Length;
            HideSuggestions();
        }
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Curated quick-pick set for the emoji flyout; Win + . remains the full picker.</summary>
    private static readonly string[] QuickEmojis =
    {
        "😀", "😄", "😂", "🤣", "😊", "😉", "😍", "🥰", "😎", "🤓", "🤔", "🙃",
        "😅", "😬", "😭", "🥳", "🤯", "😴", "🙄", "😤", "😱", "🫠", "🤗", "🫡",
        "👍", "👎", "👌", "🙏", "👏", "💪", "🤝", "✌️", "🤞", "👀", "🧠", "💯",
        "🔥", "✨", "🚀", "🎉", "🎯", "💡", "⚡", "⭐", "❤️", "💔", "✅", "❌",
        "⚠️", "❓", "❗", "💬", "🐛", "🔧", "🔒", "🔑", "📝", "📌", "📁", "🖥️",
        "☕", "🍕", "🎮", "🤖",
    };

    /// <summary>Slack-style shortcode → emoji. Aliases are separate rows pointing at the same
    /// emoji. Names must be lowercase; lookup lowercases the typed fragment.</summary>
    private static readonly (string Name, string Emoji)[] EmojiShortcodes =
    {
        ("grinning", "😀"), ("smile", "😄"), ("joy", "😂"), ("rofl", "🤣"),
        ("blush", "😊"), ("wink", "😉"), ("heart_eyes", "😍"), ("smiling_hearts", "🥰"),
        ("sunglasses", "😎"), ("coolglasses", "😎"), ("nerd", "🤓"), ("thinking", "🤔"),
        ("upside_down", "🙃"), ("sweat_smile", "😅"), ("grimacing", "😬"), ("sob", "😭"),
        ("partying", "🥳"), ("mind_blown", "🤯"), ("sleeping", "😴"), ("eye_roll", "🙄"),
        ("triumph", "😤"), ("scream", "😱"), ("melting", "🫠"), ("hugs", "🤗"),
        ("salute", "🫡"), ("thumbsup", "👍"), ("+1", "👍"), ("thumbsdown", "👎"),
        ("-1", "👎"), ("ok_hand", "👌"), ("pray", "🙏"), ("clap", "👏"),
        ("muscle", "💪"), ("handshake", "🤝"), ("victory", "✌️"), ("crossed_fingers", "🤞"),
        ("eyes", "👀"), ("brain", "🧠"), ("100", "💯"), ("fire", "🔥"),
        ("sparkles", "✨"), ("rocket", "🚀"), ("tada", "🎉"), ("party_popper", "🎉"),
        ("dart", "🎯"), ("bulb", "💡"), ("idea", "💡"), ("zap", "⚡"),
        ("star", "⭐"), ("heart", "❤️"), ("broken_heart", "💔"), ("check", "✅"),
        ("white_check_mark", "✅"), ("x", "❌"), ("cross", "❌"), ("warning", "⚠️"),
        ("question", "❓"), ("exclamation", "❗"), ("speech_balloon", "💬"), ("bug", "🐛"),
        ("wrench", "🔧"), ("lock", "🔒"), ("key", "🔑"), ("memo", "📝"),
        ("note", "📝"), ("pushpin", "📌"), ("pin", "📌"), ("folder", "📁"),
        ("desktop", "🖥️"), ("coffee", "☕"), ("pizza", "🍕"), ("video_game", "🎮"),
        ("robot", "🤖"),
    };

    private void EmojiGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string emoji || !InputBox.IsEnabled) return;
        var caret = Math.Min(InputBox.SelectionStart, InputBox.Text.Length);
        InputBox.Text = InputBox.Text.Insert(caret, emoji);
        InputBox.SelectionStart = caret + emoji.Length;
        InputBox.Focus(FocusState.Programmatic);
    }

    private void HideSuggestions()
    {
        _suggestMode = SuggestMode.None;
        SuggestionsPanel.Visibility = Visibility.Collapsed;
        _suggestions.Clear();
    }

    // ============================================================
    // IApprovalUi — this tab's approval overlay (completes the harness's awaited TCS)
    // ============================================================

    public Task<string> ShowApprovalAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalTcs = tcs;

        var reg = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                OnUi(() => { HideApprovalOverlay(); HidePlanApprovalBar(); });
            })
            : default(CancellationTokenRegistration);

        OnUi(() =>
        {
            // What the cross-tab toast will say — a specific "what's waiting" line, not the modal's
            // question. Set for both the bottom-bar and modal paths.
            _approvalSummary = string.IsNullOrEmpty(request.ToastSummary) ? request.Title : request.ToastSummary;

            // Plan approvals render as a non-covering bottom bar so the plan card stays readable.
            if (request.BottomBar)
            {
                ShowPlanApprovalBar(request, choice =>
                {
                    HidePlanApprovalBar();
                    reg.Dispose();
                    tcs.TrySetResult(choice);
                    if (ReferenceEquals(_approvalTcs, tcs)) _approvalTcs = null;
                });
                return;
            }

            ApprovalTitle.Text = request.Title;

            ApprovalSubtitle.Text = request.Subtitle ?? "";
            ApprovalSubtitle.Visibility = string.IsNullOrEmpty(request.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

            ApprovalDetail.Text = request.Detail ?? "";
            ApprovalDetail.Visibility = string.IsNullOrEmpty(request.Detail) ? Visibility.Collapsed : Visibility.Visible;

            // Pull the shared, theme-mutated brushes from app resources so the approval diff
            // follows the active theme (these used to be hardcoded LightSkyBlue/red/gray, which
            // stayed blue under every theme — jarring under E-Ink). Mirrors the transcript's
            // diff coloring: command/added -> sky, removed -> red, context -> dim.
            var skyBrush = (SolidColorBrush)Application.Current.Resources["MandoSkyBrush"];
            var redBrush = (SolidColorBrush)Application.Current.Resources["MandoRedBrush"];
            var dimBrush = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];

            var rows = new List<DiffLineVm>();
            if (request.CommandText != null)
            {
                rows.Add(new DiffLineVm
                {
                    Text = $"$ {request.CommandText}",
                    Brush = skyBrush
                });
            }
            if (request.DiffLines != null)
            {
                foreach (var line in request.DiffLines)
                {
                    var (prefix, brush) = line.LineType switch
                    {
                        DiffLineType.Added => ("+ ", skyBrush),
                        DiffLineType.Removed => ("- ", redBrush),
                        _ => ("  ", dimBrush)
                    };
                    var num = (line.LineType == DiffLineType.Added ? line.NewLineNumber : line.OldLineNumber);
                    rows.Add(new DiffLineVm
                    {
                        Text = $"{(num.HasValue ? num.Value.ToString().PadLeft(4) : "    ")} {prefix}{line.Content}",
                        Brush = brush
                    });
                }
                if (request.DiffSummary != null)
                    rows.Add(new DiffLineVm { Text = "", Brush = dimBrush });
            }
            ApprovalDiffList.ItemsSource = rows;
            ApprovalBodyScroll.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrEmpty(request.DiffSummary))
            {
                ApprovalDetail.Text = request.DiffSummary;
                ApprovalDetail.Visibility = Visibility.Visible;
            }

            ApprovalButtons.Children.Clear();
            foreach (var option in request.Options)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                if (!string.IsNullOrEmpty(option.Glyph))
                    content.Children.Add(new FontIcon { Glyph = option.Glyph, FontSize = 13 });
                content.Children.Add(new TextBlock { Text = option.Label });
                var button = new Button
                {
                    Content = content,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = option.Label
                };
                button.Foreground = option.Kind switch
                {
                    ApprovalOptionKind.Proceed => (SolidColorBrush)Application.Current.Resources["MandoGreenBrush"],
                    ApprovalOptionKind.Destructive => (SolidColorBrush)Application.Current.Resources["MandoRedBrush"],
                    _ => (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"]
                };
                if (!string.IsNullOrEmpty(option.Description))
                    ToolTipService.SetToolTip(button, option.Description);
                button.Click += (_, _) =>
                {
                    var choice = (string)button.Tag;
                    HideApprovalOverlay();
                    reg.Dispose();
                    _approvalTcs?.TrySetResult(choice);
                    _approvalTcs = null;
                };
                ApprovalButtons.Children.Add(button);
            }

            InstructionPanel.Visibility = Visibility.Collapsed;
            ApprovalButtons.Visibility = Visibility.Visible;
            SetApprovalCardSize(instructionMode: false);
            ShowApprovalOverlay();
        });

        return tcs.Task;
    }

    /// <summary>Approval mode: compact centered card. Instruction mode: full width and
    /// half the window height, centered — room to write real instructions.</summary>
    private void SetApprovalCardSize(bool instructionMode)
    {
        if (instructionMode)
        {
            ApprovalCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApprovalCard.MaxWidth = double.PositiveInfinity;
            ApprovalCard.MaxHeight = double.PositiveInfinity;
            ApprovalCard.Height = Math.Max(320, ChatRoot.ActualHeight * 0.5);
        }
        else
        {
            ApprovalCard.HorizontalAlignment = HorizontalAlignment.Center;
            ApprovalCard.MaxWidth = 860;
            ApprovalCard.MaxHeight = 640;
            ApprovalCard.Height = double.NaN;
        }
    }

    public Task<string> ShowInstructionInputAsync(string prompt, string placeholder = "", bool allowCancel = false, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _instructionTcs = tcs;

        OnUi(() =>
        {
            // First line is the question; any extra lines (e.g. a validation error on
            // re-prompt) render below it in the smaller prompt text.
            var newline = prompt.IndexOf('\n');
            ApprovalTitle.Text = newline < 0 ? prompt : prompt[..newline];
            InstructionPrompt.Text = newline < 0 ? "" : prompt[(newline + 1)..].Trim();
            InstructionPrompt.Visibility = InstructionPrompt.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            ApprovalSubtitle.Visibility = Visibility.Collapsed;
            ApprovalDetail.Visibility = Visibility.Collapsed;
            ApprovalBodyScroll.Visibility = Visibility.Collapsed;
            ApprovalButtons.Visibility = Visibility.Collapsed;

            InstructionBox.Text = "";
            InstructionBox.PlaceholderText = string.IsNullOrEmpty(placeholder)
                ? "Type your answer and press Enter"
                : placeholder;
            InstructionCancelButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            InstructionPanel.Visibility = Visibility.Visible;
            SetApprovalCardSize(instructionMode: true);
            ShowApprovalOverlay();
            InstructionBox.Focus(FocusState.Programmatic);
        });

        return tcs.Task;
    }

    private void InstructionBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Shift+Enter inserts a newline (the box is multi-line); plain Enter submits.
            // PreviewKeyDown is required here — with AcceptsReturn, the class handler
            // would insert the newline before a plain KeyDown handler ever ran.
            var shift = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            SubmitInstruction();
        }
        else if (e.Key == VirtualKey.Escape && InstructionCancelButton.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            CancelInstruction();
        }
    }

    private void InstructionSubmit_Click(object sender, RoutedEventArgs e) => SubmitInstruction();

    private void InstructionCancel_Click(object sender, RoutedEventArgs e) => CancelInstruction();

    private void SubmitInstruction()
    {
        var text = InstructionBox.Text;
        HideApprovalOverlay();
        _instructionTcs?.TrySetResult(text);
        _instructionTcs = null;
    }

    private void CancelInstruction()
    {
        HideApprovalOverlay();
        _instructionTcs?.TrySetResult(ApprovalSignals.Cancelled);
        _instructionTcs = null;
    }

    /// <summary>An approval raised in a background tab can't steal focus, so MainWindow badges
    /// that tab and raises the toast instead.</summary>
    private void ShowApprovalOverlay()
    {
        ApprovalOverlay.Visibility = Visibility.Visible;
        ApprovalStateChanged?.Invoke(this);
    }

    private void HideApprovalOverlay()
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDiffList.ItemsSource = null;
        ApprovalStateChanged?.Invoke(this);
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Slides the plan-approval bar up above the input. Unlike the modal it doesn't cover the
    /// transcript (the plan stays readable), but it DOES gate input — the turn is awaiting the choice.</summary>
    private void ShowPlanApprovalBar(ApprovalRequest request, Action<string> onChosen)
    {
        // Windows 98 theme: the bar drops its rounded card look and reads as a silver
        // dialog strip — square corners, dialog-face background. Rebuilt on every show,
        // so live theme switches take effect on the next approval.
        var win98 = ThemeManager.Current.Win98;
        PlanApprovalBar.CornerRadius = new CornerRadius(win98 ? 0 : 12);
        PlanApprovalBar.Background = (Brush)Application.Current.Resources[
            win98 ? "MandoBackgroundBrush" : "MandoPanelBrush"];

        PlanApprovalTitle.Text = request.Title;

        // Command approvals ride this bar too: show the command in monospace. The buttons
        // live in a WrapPanel — one horizontal row whenever it fits, wrapping only when the
        // window is too narrow for the long "don't ask again" labels.
        PlanApprovalCommand.Text = string.IsNullOrEmpty(request.CommandText) ? "" : "$ " + request.CommandText;
        PlanApprovalCommand.Visibility = string.IsNullOrEmpty(request.CommandText)
            ? Visibility.Collapsed : Visibility.Visible;

        PlanApprovalButtons.Children.Clear();
        foreach (var option in request.Options)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (!string.IsNullOrEmpty(option.Glyph))
                content.Children.Add(new FontIcon { Glyph = option.Glyph, FontSize = 13 });
            content.Children.Add(new TextBlock { Text = option.Label });

            var button = new Button { Content = content, Tag = option.Label, Padding = new Thickness(14, 6, 14, 6) };
            if (win98) button.CornerRadius = new CornerRadius(0);   // square, like every 98 control
            if (option.Kind == ApprovalOptionKind.Proceed)
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];   // primary
            else
                button.Foreground = option.Kind == ApprovalOptionKind.Destructive
                    ? (SolidColorBrush)Application.Current.Resources["MandoRedBrush"]
                    : (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
            if (!string.IsNullOrEmpty(option.Description))
                ToolTipService.SetToolTip(button, option.Description);
            button.Click += (_, _) => onChosen((string)button.Tag);
            PlanApprovalButtons.Children.Add(button);
        }

        // Gate input while the plan is awaiting a decision.
        InputBox.IsEnabled = false;
        SendButton.IsEnabled = false;
        EmojiButton.IsEnabled = false;

        PlanApprovalBar.Visibility = Visibility.Visible;
        ApprovalStateChanged?.Invoke(this);

        var slide = new DoubleAnimation
        {
            From = 24, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, PlanApprovalTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Storyboard.SetTarget(fade, PlanApprovalBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Children.Add(fade);
        sb.Begin();
    }

    private void HidePlanApprovalBar()
    {
        if (PlanApprovalBar.Visibility != Visibility.Visible) return;
        PlanApprovalBar.Visibility = Visibility.Collapsed;
        InputBox.IsEnabled = true;
        SendButton.IsEnabled = true;
        EmojiButton.IsEnabled = true;
        ApprovalStateChanged?.Invoke(this);
        InputBox.Focus(FocusState.Programmatic);
    }
}

/// <summary>One row in the header's model dropdown: the model tag plus a cloud/local badge.
/// Built on the UI thread when the flyout opens, so it can carry ready-made brushes.</summary>
public sealed class ModelItem
{
    public ModelItem(string name, string badge, Brush badgeForeground, Brush badgeBackground)
    {
        Name = name;
        Badge = badge;
        BadgeForeground = badgeForeground;
        BadgeBackground = badgeBackground;
    }

    public string Name { get; }
    public string Badge { get; }
    public Brush BadgeForeground { get; }
    public Brush BadgeBackground { get; }
}

/// <summary>One row in the file-explorer tree. Folder nodes are created with unrealized
/// children and lazy-load their contents on first expand (ChatTabView.ExplorerTree_Expanding).</summary>
public sealed class ExplorerItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; private init; } = "";
    public string FullPath { get; private init; } = "";
    public bool IsDirectory { get; private init; }

    /// <summary>Root-relative path with forward slashes \u2014 the key used to match this row
    /// against git change entries.</summary>
    public string RelPath { get; private init; } = "";

    /// <summary>The exact @token the row produces (root-relative, forward slashes, trailing
    /// '/' on folders) \u2014 shown in the tag button's tooltip so hovering teaches the @ syntax.</summary>
    public string Token { get; private init; } = "";

    public string TagTooltip => $"Tag in prompt \u2014 inserts {Token}";

    /// <summary>Files: this file has uncommitted changes. Folders: something inside does.
    /// Mutable + observable so rows already realized in the tree light up in place when a
    /// git refresh lands (rebuilding the tree would lose expansion state).</summary>
    public bool Dirty
    {
        get => _dirty;
        set
        {
            if (_dirty == value) return;
            _dirty = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DirtyVisibility)));
        }
    }
    private bool _dirty;

    public Visibility DirtyVisibility => _dirty ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";   // folder / document

    /// <summary>Resolved per-realization from app resources, so icons pick up live theme
    /// switches the next time rows are created (matching how transcript colors retheme).</summary>
    public Brush? IconBrush =>
        Application.Current.Resources[IsDirectory ? "MandoGoldBrush" : "MandoDimBrush"] as Brush;

    public static ExplorerItem ForFolder(string path, string root)
    {
        var rel = Rel(path, root);
        return new() { Name = Path.GetFileName(path), FullPath = path, IsDirectory = true, RelPath = rel, Token = "@" + rel + "/" };
    }

    public static ExplorerItem ForFile(string path, string root)
    {
        var rel = Rel(path, root);
        return new() { Name = Path.GetFileName(path), FullPath = path, IsDirectory = false, RelPath = rel, Token = "@" + rel };
    }

    private static string Rel(string path, string root) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>One row in the explorer's Changes tab: a working-tree change with its display
/// letter/color, split name + directory, and the @token its tag button inserts. Built on
/// the UI thread from a GitQuickStatus snapshot, so it carries ready-made brushes
/// (same pattern as ModelItem).</summary>
public sealed class GitChangeItem
{
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public Brush? KindBrush { get; init; }
    public string Name { get; init; } = "";
    public string Dir { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string RelPath { get; init; } = "";
    public string TagTooltip { get; init; } = "";

    /// <summary>Undo restores from HEAD, so it needs a HEAD side: hidden for untracked rows
    /// ("undoing" a new file would DELETE it — different action, different UI) and renamed
    /// rows (a clean rename-undo needs both paths).</summary>
    public Visibility UndoVisibility => Kind is "M" or "D" or "!" ? Visibility.Visible : Visibility.Collapsed;

    public string UndoTooltip => Kind == "D"
        ? "Restore this deleted file"
        : "Undo changes — restore this file to the last commit (asks first)";
}
