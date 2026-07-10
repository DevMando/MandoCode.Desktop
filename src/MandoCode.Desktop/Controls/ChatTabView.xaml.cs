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

    private enum SuggestMode { None, Command, File }
    private SuggestMode _suggestMode = SuggestMode.None;
    private int _tokenStart;   // index of the '@' (File mode) — replaced on accept
    private int _tokenEnd;     // caret position when suggestions were computed

    public AgentSession Session { get; }

    private ChatController _controller => Session.Controller;
    private TranscriptWriter _transcript => Session.Transcript;
    private MandoCode.Services.FileAutocompleteProvider _fileProvider => Session.FileProvider;

    /// <summary>True while this tab's approval overlay is up and awaiting a choice.</summary>
    public bool IsApprovalOpen => ApprovalOverlay.Visibility == Visibility.Visible;

    /// <summary>The pending approval's headline — MainWindow shows it in the cross-tab toast.</summary>
    public string ApprovalHeadline => ApprovalTitle.Text;

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
            core.NavigationCompleted += (_, _) =>
            {
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

    /// <summary>Snapshots the transcript document to a standalone .html file. The highlight
    /// classes and CSS are already baked into the DOM, so the saved page keeps its colors.</summary>
    private void SaveTranscript_Click(object sender, RoutedEventArgs e) => _ = ExportTranscriptAsync();

    /// <summary>Manually snapshot this tab's conversation (the "Take snapshot" tab action).</summary>
    public void TakeSnapshotManually() => _ = _controller.CaptureManualSnapshotAsync();

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

        HeaderChanged?.Invoke(this);
    }

    private void UpdateBusy(bool busy, string? activity)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = busy;
        if (busy) BusyText.Text = string.IsNullOrWhiteSpace(activity) ? "Working..." : activity;
    }

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
        else
        {
            InputBox.Text = s.Command + " ";
            InputBox.SelectionStart = InputBox.Text.Length;
            HideSuggestions();
        }
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
                OnUi(HideApprovalOverlay);
            })
            : default(CancellationTokenRegistration);

        OnUi(() =>
        {
            ApprovalTitle.Text = request.Title;

            ApprovalSubtitle.Text = request.Subtitle ?? "";
            ApprovalSubtitle.Visibility = string.IsNullOrEmpty(request.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

            ApprovalDetail.Text = request.Detail ?? "";
            ApprovalDetail.Visibility = string.IsNullOrEmpty(request.Detail) ? Visibility.Collapsed : Visibility.Visible;

            var rows = new List<DiffLineVm>();
            if (request.CommandText != null)
            {
                rows.Add(new DiffLineVm
                {
                    Text = $"$ {request.CommandText}",
                    Brush = new SolidColorBrush(Colors.LightSkyBlue)
                });
            }
            if (request.DiffLines != null)
            {
                foreach (var line in request.DiffLines)
                {
                    var (prefix, color) = line.LineType switch
                    {
                        DiffLineType.Added => ("+ ", Colors.LightSkyBlue),
                        DiffLineType.Removed => ("- ", Windows.UI.Color.FromArgb(255, 224, 82, 82)),
                        _ => ("  ", Colors.Gray)
                    };
                    var num = (line.LineType == DiffLineType.Added ? line.NewLineNumber : line.OldLineNumber);
                    rows.Add(new DiffLineVm
                    {
                        Text = $"{(num.HasValue ? num.Value.ToString().PadLeft(4) : "    ")} {prefix}{line.Content}",
                        Brush = new SolidColorBrush(color)
                    });
                }
                if (request.DiffSummary != null)
                    rows.Add(new DiffLineVm { Text = "", Brush = new SolidColorBrush(Colors.Gray) });
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
