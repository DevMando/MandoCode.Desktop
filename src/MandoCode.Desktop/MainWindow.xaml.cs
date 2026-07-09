using System.Collections.ObjectModel;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

/// <summary>Row model for the slash-command suggestions list.</summary>
public sealed class CommandSuggestion
{
    public string Command { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Row model for diff lines shown in the approval overlay.</summary>
public sealed class DiffLineVm
{
    public string Text { get; init; } = "";
    public SolidColorBrush Brush { get; init; } = new(Colors.Gray);
}

/// <summary>Row model for the MCP servers page.</summary>
public sealed class McpRow
{
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Status { get; init; } = "";
    public SolidColorBrush StatusBrush { get; init; } = new(Colors.Gray);
}

/// <summary>Chip model for the MCP editor's tool preview (test results).</summary>
public sealed class ToolChip
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Row model for the Appearance tab's theme picker — each card is drawn in
/// its own theme's colors so the list doubles as a preview.</summary>
public sealed class ThemeVm
{
    public required UiTheme Theme { get; init; }
    public string Name => Theme.Name;
    public string Description => Theme.Description;
    public SolidColorBrush BgBrush => new(ThemeManager.C(Theme.Background));
    public SolidColorBrush EdgeBrush => new(ThemeManager.C(Theme.Border));
    public SolidColorBrush FgBrush => new(ThemeManager.C(Theme.Text));
    public SolidColorBrush DimBrush => new(ThemeManager.C(Theme.Dim));
    public SolidColorBrush AccentBrush => new(ThemeManager.C(Theme.Accent));
    public SolidColorBrush GoldBrush => new(ThemeManager.C(Theme.Gold));
    public SolidColorBrush SkyBrush => new(ThemeManager.C(Theme.Sky));
    public SolidColorBrush GreenBrush => new(ThemeManager.C(Theme.Green));
}

public sealed partial class MainWindow : Window, IApprovalUi
{
    private readonly ChatController _controller;
    private readonly TranscriptWriter _transcript;
    private readonly TranscriptHtmlBuilder _html;
    private readonly BusyStateService _busy;
    private readonly WinUiApprovalService _approvals;
    private readonly MandoCode.Services.ProjectRootAccessor _projectRoot;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private readonly Queue<string> _pendingHtml = new();
    private bool _webViewReady;

    private TaskCompletionSource<string>? _approvalTcs;
    private TaskCompletionSource<string>? _instructionTcs;

    private readonly ObservableCollection<CommandSuggestion> _suggestions = new();
    private readonly MandoCode.Services.FileAutocompleteProvider _fileProvider;

    private enum SuggestMode { None, Command, File }
    private SuggestMode _suggestMode = SuggestMode.None;
    private int _tokenStart;   // index of the '@' (File mode) — replaced on accept
    private int _tokenEnd;     // caret position when suggestions were computed

    public MainWindow()
    {
        InitializeComponent();
        Title = "MandoCode Desktop";

        ThemeManager.Initialize(Root);
        ThemeManager.ThemeChanged += () => OnUi(ApplyThemeToTranscript);
        TranscriptView.DefaultBackgroundColor = ThemeManager.C(ThemeManager.Current.Background);
        SettingsTabs.SelectedItem = Tab_Appearance;
        ThemeList.ItemsSource = UiTheme.All.Select(t => new ThemeVm { Theme = t }).ToList();
        ModelCombo.Loaded += (_, _) => ApplyModelComboTarget();
        S_WindowOpacity.Value = ThemeManager.WindowOpacity * 100;
        ApplyWindowOpacity(ThemeManager.WindowOpacity);

        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var services = App.Services;
        _controller = services.GetRequiredService<ChatController>();
        _transcript = services.GetRequiredService<TranscriptWriter>();
        _html = services.GetRequiredService<TranscriptHtmlBuilder>();
        _busy = services.GetRequiredService<BusyStateService>();
        _approvals = services.GetRequiredService<WinUiApprovalService>();
        _projectRoot = services.GetRequiredService<MandoCode.Services.ProjectRootAccessor>();
        _fileProvider = services.GetRequiredService<MandoCode.Services.FileAutocompleteProvider>();

        _approvals.Ui = this;
        SuggestionsList.ItemsSource = _suggestions;

        // Harness events → UI thread
        _transcript.BlockAdded += html => OnUi(() => AppendHtml(html));
        _transcript.Cleared += () => OnUi(ClearTranscript);
        _busy.Changed += (busy, activity) => OnUi(() => UpdateBusy(busy, activity));
        _controller.StateChanged += () => OnUi(UpdateStatusBar);
        _controller.PlanProgressChanged += (done, total, active) => OnUi(() => UpdatePlanProgress(done, total, active));
        _controller.SetupNeeded += () => OnUi(() => SwitchPage("settings"));
        _controller.McpEditorRequested += serverName => OnUi(() =>
        {
            SwitchPage("mcp");
            OpenMcpEditor(serverName);
        });
        _controller.ClipboardCopyRequested += text => OnUi(() => CopyToClipboard(text));
        _controller.ExitRequested += () => OnUi(Close);

        UpdateStatusBar();
        SwitchPage("chat");

        // Size the window; defer WebView2 + harness init until the tree is loaded.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 840));
        Root.Loaded += Root_Loaded;
    }

    private bool _initialized;

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        RootLoadedAsync();
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private async void RootLoadedAsync()
    {
        try
        {
            await TranscriptView.EnsureCoreWebView2Async();
            TranscriptView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            TranscriptView.CoreWebView2.Settings.AreDevToolsEnabled = true;   // F12 in the transcript — invaluable for debugging the HTML pipeline
            TranscriptView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webViewReady = true;
                while (_pendingHtml.Count > 0) AppendHtml(_pendingHtml.Dequeue());
            };

            // The WebView hosts only the transcript document. Any link click (update
            // notice, markdown links in responses) opens in the default browser instead
            // of navigating the transcript away.
            TranscriptView.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenInBrowser(e.Uri);
                }
            };
            TranscriptView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenInBrowser(e.Uri);
            };

            // File-path links in transcript cards post "open-file:<path>" messages
            // (see TranscriptHtmlBuilder.FileLink) — the desktop counterpart of the
            // CLI's clickable terminal hyperlinks.
            TranscriptView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string? msg = null;
                try { msg = e.TryGetWebMessageAsString(); } catch { /* non-string message — not ours */ }
                if (msg != null && msg.StartsWith("open-file:", StringComparison.Ordinal))
                    OpenTranscriptPath(msg["open-file:".Length..]);
                else if (msg != null && msg.StartsWith("copy:", StringComparison.Ordinal))
                    CopyToClipboard(msg["copy:".Length..]);
            };
            // Serve bundled web assets (highlight.js) to the transcript document.
            // Missing folder just means syntax highlighting silently doesn't engage.
            try
            {
                TranscriptView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "mandocode.assets",
                    Path.Combine(AppContext.BaseDirectory, "Assets", "web"),
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            TranscriptView.CoreWebView2.NavigateToString(TranscriptHtmlBuilder.BaseDocument(ThemeManager.Current));
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing — extremely rare on Win11, but fail visibly.
            ModelText.Text = $"WebView2 init failed: {ex.Message}";
        }

        InputBox.Focus(FocusState.Programmatic);
        await Task.Run(_controller.InitializeAsync);
    }

    /// <summary>Snapshots the transcript document to a standalone .html file. The
    /// highlight classes and CSS are already baked into the DOM, so the saved page
    /// keeps its colors with no script dependencies.</summary>
    private async void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady) return;
        try
        {
            var json = await TranscriptView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            var html = JsonSerializer.Deserialize<string>(json) ?? "";

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
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

    /// <summary>Opens a clicked transcript path with its default app (folders open in
    /// Explorer). Relative paths — how operation cards display them — resolve against
    /// the current project root.</summary>
    private void OpenTranscriptPath(string raw)
    {
        try
        {
            var path = raw.Trim();
            if (!Path.IsPathRooted(path)) path = Path.Combine(_projectRoot.ProjectRoot, path);
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

    // ============================================================
    // Transcript
    // ============================================================

    private async void AppendHtml(string html)
    {
        if (!_webViewReady)
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
        if (!_webViewReady) return;
        try { await TranscriptView.CoreWebView2.ExecuteScriptAsync("window.__clear()"); }
        catch { }
    }

    // ============================================================
    // Status / busy / plan progress
    // ============================================================

    private void UpdateStatusBar()
    {
        ModelText.Text = _controller.ModelName;
        ProjectRootText.Text = _controller.ProjectRootPath;
        ConnectionDot.Fill = new SolidColorBrush(
            _controller.ModelError ? Colors.Orange
            : _controller.IsConnected ? Colors.LimeGreen
            : Colors.Gray);

        var tracker = App.Services.GetRequiredService<MandoCode.Services.TokenTrackingService>();
        TokenText.Text = tracker.TotalSessionTokens > 0
            ? $"{MandoCode.Services.TokenTrackingService.FormatTokenCount(tracker.TotalSessionTokens)} tokens"
            : "";

        var processing = _controller.IsProcessing;
        SendIcon.Glyph = processing ? "" : "";   // stop vs send
        SendLabel.Text = processing ? "Stop" : "Send";
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
        UpdateStatusBar();

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

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ApprovalOverlay.Visibility != Visibility.Visible)
            _controller.CancelActiveRequest();
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
    // IApprovalUi — approval overlay (completes the harness's awaited TCS)
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
            ApprovalOverlay.Visibility = Visibility.Visible;
            _approvalToastDismissed = false;   // each new approval earns a fresh toast
            RefreshNavIcons();
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
            ApprovalCard.Height = Math.Max(320, Root.ActualHeight * 0.5);
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
            ApprovalOverlay.Visibility = Visibility.Visible;
            _approvalToastDismissed = false;
            RefreshNavIcons();
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

    private void HideApprovalOverlay()
    {
        ApprovalOverlay.Visibility = Visibility.Collapsed;
        ApprovalDiffList.ItemsSource = null;
        _approvalToastDismissed = false;
        RefreshNavIcons();
        InputBox.Focus(FocusState.Programmatic);
    }

    // ============================================================
    // Sidebar navigation
    // ============================================================

    private string _currentPage = "chat";

    /// <summary>The approval overlay lives inside the chat page, so an approval that
    /// arrives while you're on Settings/MCP doesn't interrupt — the chat icon turns
    /// gold instead, and the modal is waiting when you switch back.</summary>
    private bool _approvalToastDismissed;

    private void RefreshNavIcons()
    {
        var accent = (SolidColorBrush)Application.Current.Resources["MandoAccentBrush"];
        var normal = (SolidColorBrush)Application.Current.Resources["MandoDimBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
        var approvalPending = ApprovalOverlay.Visibility == Visibility.Visible && _currentPage != "chat";

        NavChatIcon.Foreground = _currentPage == "chat" ? accent : (approvalPending ? gold : normal);
        NavSettingsIcon.Foreground = _currentPage == "settings" ? accent : normal;
        NavMcpIcon.Foreground = _currentPage == "mcp" ? accent : normal;
        ToolTipService.SetToolTip(NavChat, approvalPending ? "Chat — approval waiting" : "Chat");

        // The toast mirrors the same pending state: it appears when an approval shows
        // while you're on another page, and clears the moment you reach chat or the
        // approval resolves. Dismissing it leaves the gold icon as the passive cue.
        if (approvalPending) ApprovalToastText.Text = ApprovalTitle.Text;
        ApprovalToast.Visibility = approvalPending && !_approvalToastDismissed
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApprovalToast_Tapped(object sender, TappedRoutedEventArgs e) => SwitchPage("chat");

    private void ApprovalToastDismiss_Click(object sender, RoutedEventArgs e)
    {
        _approvalToastDismissed = true;
        ApprovalToast.Visibility = Visibility.Collapsed;
    }

    private void NavChat_Click(object sender, RoutedEventArgs e) => SwitchPage("chat");
    private void NavSettings_Click(object sender, RoutedEventArgs e) => SwitchPage("settings");
    private void NavMcp_Click(object sender, RoutedEventArgs e) => SwitchPage("mcp");

    private void SwitchPage(string page)
    {
        _currentPage = page;
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        McpPage.Visibility = page == "mcp" ? Visibility.Visible : Visibility.Collapsed;

        RefreshNavIcons();

        if (page == "settings")
        {
            LoadSettings();
            _ = RefreshModelListAsync();
        }
        else if (page == "mcp")
        {
            _ = RefreshMcpListAsync();
        }
        else
        {
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    // ============================================================
    // Settings page
    // ============================================================

    private bool _loadingSettings;

    /// <summary>Populates every control from the live config. Guarded so control-change
    /// events fired during population don't write back.</summary>
    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var cfg = _controller.Config;
            EndpointBox.Text = cfg.OllamaEndpoint;
            _modelComboTarget = cfg.GetEffectiveModelName();
            ApplyModelComboTarget();
            S_ContextLength.Value = cfg.ContextLength;
            S_Temperature.Value = cfg.Temperature;
            S_TemperatureLabel.Text = cfg.Temperature.ToString("0.##");
            S_MaxTokens.Value = cfg.MaxTokens;
            S_Streaming.SelectedItem = cfg.ResponseStreaming;
            S_TaskPlanning.IsOn = cfg.EnableTaskPlanning;
            S_DiffApprovals.IsOn = cfg.EnableDiffApprovals;
            S_AutoContinue.IsOn = cfg.EnableAutoContinuation;
            S_MaxContinuations.Value = cfg.MaxAutoContinuations;
            S_RequestTimeout.Value = cfg.RequestTimeoutMinutes;
            S_StallTimeout.Value = cfg.ModelResponseTimeoutSeconds;
            S_ToolBudget.Value = cfg.ToolResultCharBudget;
            S_RenderTimeout.Value = cfg.MarkdownRenderTimeoutSeconds;
            S_WebSearch.IsOn = cfg.EnableWebSearch;
            S_TavilyKey.Password = cfg.TavilyApiKey ?? "";
            S_TavilyKey.PasswordRevealMode = PasswordRevealMode.Hidden;
            TavilyViewButton.Content = "View";
            TavilyViewButton.IsEnabled = !string.IsNullOrEmpty(cfg.TavilyApiKey);
            S_Mcp.IsOn = cfg.EnableMcp;
            for (int i = 0; i < UiTheme.All.Count; i++)
                if (UiTheme.All[i] == ThemeManager.Current) ThemeList.SelectedIndex = i;
            SettingsStatus.Text = "";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SettingsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var s = sender.SelectedItem;
        TabPanel_Connection.Visibility = s == Tab_Connection ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Generation.Visibility = s == Tab_Generation ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Behavior.Visibility = s == Tab_Behavior ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Limits.Visibility = s == Tab_Limits ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Integrations.Visibility = s == Tab_Integrations ? Visibility.Visible : Visibility.Collapsed;
        TabPanel_Appearance.Visibility = s == Tab_Appearance ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WindowOpacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // The slider's XAML Value fires this during InitializeComponent, before the
        // label (declared after it) exists — nothing to update yet, the constructor
        // applies the persisted opacity right after the tree is built.
        if (S_WindowOpacityLabel is null) return;
        S_WindowOpacityLabel.Text = $"{(int)e.NewValue}%";
        ThemeManager.SetWindowOpacity(e.NewValue / 100.0);
        ApplyWindowOpacity(ThemeManager.WindowOpacity);
    }

    // WinUI has no Window.Opacity — whole-window translucency is a Win32 layered-window
    // attribute on the HWND. At 100% the layered style is removed entirely so the
    // compositor does no extra work for the default solid window.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const uint LWA_ALPHA = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    private void ApplyWindowOpacity(double opacity)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (opacity >= 0.995)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle & ~(nint)WS_EX_LAYERED);
        }
        else
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle | (nint)WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(opacity * 255), LWA_ALPHA);
        }
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeList.SelectedItem is not ThemeVm vm) return;
        ThemeManager.Apply(vm.Theme, Root);
        SettingsStatus.Text = $"Theme set to {vm.Theme.Name}.";
    }

    /// <summary>Recolors the WebView2 transcript in place (CSS variables) when the theme changes.</summary>
    private async void ApplyThemeToTranscript()
    {
        var theme = ThemeManager.Current;
        TranscriptView.DefaultBackgroundColor = ThemeManager.C(theme.Background);
        if (_webViewReady)
        {
            try { await TranscriptView.CoreWebView2.ExecuteScriptAsync(ThemeManager.BuildTranscriptScript(theme)); }
            catch { /* WebView gone (window closing) — nothing to recolor */ }
        }
    }

    private string _modelComboTarget = "";

    /// <summary>An editable ComboBox drops programmatic Text while its template isn't
    /// loaded (the Settings page starts collapsed) — so the intended model name is kept
    /// here and re-applied on the combo's Loaded event. Selecting the matching pulled
    /// model when one exists also marks it in the dropdown.</summary>
    private void ApplyModelComboTarget()
    {
        if (_modelComboTarget.Length == 0) return;
        if (ModelCombo.ItemsSource is IList<string> models)
        {
            var idx = models.IndexOf(_modelComboTarget);
            if (idx >= 0)
            {
                ModelCombo.SelectedIndex = idx;
                return;
            }
        }
        ModelCombo.Text = _modelComboTarget;
    }

    /// <summary>One write path for the whole page: ConfigKeySetter via the controller.</summary>
    private async Task ApplySettingAsync(string key, string value)
    {
        var (ok, message) = await _controller.ApplyConfigKeyAsync(key, value);
        SettingsStatus.Text = message;
        if (!ok) LoadSettings();   // revert the control to the real value
    }

    private async void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var toggle = (ToggleSwitch)sender;
        await ApplySettingAsync((string)toggle.Tag, toggle.IsOn ? "true" : "false");
    }

    private async void Setting_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings || double.IsNaN(args.NewValue)) return;
        await ApplySettingAsync((string)sender.Tag, ((long)args.NewValue).ToString());
    }

    private async void Temperature_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingSettings) return;
        S_TemperatureLabel.Text = e.NewValue.ToString("0.##");
        await ApplySettingAsync("temperature", e.NewValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private async void Streaming_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || S_Streaming.SelectedItem is not string mode) return;
        await ApplySettingAsync("streaming", mode);
    }

    /// <summary>Enables View as soon as there's anything to reveal (saved key or fresh typing).</summary>
    private void TavilyKey_Changed(object sender, RoutedEventArgs e) =>
        TavilyViewButton.IsEnabled = S_TavilyKey.Password.Length > 0;

    private void TavilyView_Click(object sender, RoutedEventArgs e)
    {
        var show = S_TavilyKey.PasswordRevealMode != PasswordRevealMode.Visible;
        S_TavilyKey.PasswordRevealMode = show ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        TavilyViewButton.Content = show ? "Hide" : "View";
    }

    private async void TavilySave_Click(object sender, RoutedEventArgs e)
    {
        var key = S_TavilyKey.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            SettingsStatus.Text = "Enter a key first (or type 'clear' to remove the saved one).";
            return;
        }
        await ApplySettingAsync("tavilyKey", key.Trim());
        LoadSettings();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) =>
        await RefreshModelListAsync();

    private async Task RefreshModelListAsync()
    {
        ModelListStatus.Text = "Fetching models…";
        var models = await Task.Run(_controller.ListModelsAsync);
        if (!string.IsNullOrEmpty(ModelCombo.Text)) _modelComboTarget = ModelCombo.Text;
        ModelCombo.ItemsSource = models;
        ApplyModelComboTarget();
        ModelListStatus.Text = models.Count == 0
            ? "No models found — is Ollama running? (ollama serve, then ollama pull <model>)"
            : $"{models.Count} model(s) available.";
    }

    private async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointBox.Text;
        var model = ModelCombo.Text;
        SettingsStatus.Text = "Connecting… (details land in the chat transcript)";
        await Task.Run(() => _controller.ApplyConnectionSettingsAsync(endpoint, model));
        SettingsStatus.Text = _controller.IsConnected
            ? $"✓ Connected — {_controller.ModelName}"
            : "Couldn't connect — see the chat transcript for details.";
        LoadSettings();
    }

    // ============================================================
    // MCP page
    // ============================================================

    private async void McpRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMcpListAsync();

    private async Task RefreshMcpListAsync()
    {
        McpPageStatus.Text = "Checking server status…";
        var rows = await Task.Run(_controller.GetMcpStatusRowsAsync);

        var green = (SolidColorBrush)Application.Current.Resources["MandoGreenBrush"];
        var gold = (SolidColorBrush)Application.Current.Resources["MandoGoldBrush"];
        McpList.ItemsSource = rows.Select(r => new McpRow
        {
            Name = r.Name,
            Transport = r.Transport,
            Status = r.Status,
            StatusBrush = r.Connected ? green : gold
        }).ToList();

        McpPageStatus.Text = rows.Count == 0
            ? "No MCP servers configured yet — add one below. Same config file as the CLI."
            : $"{rows.Count} server(s) configured.";
    }

    /// <summary>Runs a slash command through the normal pipeline (transcript echo, wizard
    /// overlays, busy state all included), then refreshes the server list.</summary>
    private async Task RunMcpCommandAsync(string command)
    {
        if (_controller.IsProcessing)
        {
            McpPageStatus.Text = "Busy — wait for the current request to finish.";
            return;
        }
        await Task.Run(() => _controller.SubmitAsync(command));
        await RefreshMcpListAsync();
    }

    private void McpAdd_Click(object sender, RoutedEventArgs e) => OpenMcpEditor(null);

    private void McpEdit_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to edit first.";
            return;
        }
        OpenMcpEditor(row.Name);
    }

    private void McpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = McpList.SelectedItem is McpRow;
        McpEditButton.IsEnabled = hasSelection;
        McpRemoveButton.IsEnabled = hasSelection;
    }

    private void McpList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (McpList.SelectedItem is McpRow row) OpenMcpEditor(row.Name);
    }

    private async void McpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpRow row)
        {
            McpPageStatus.Text = "Select a server to remove first.";
            return;
        }
        await RunMcpCommandAsync($"/mcp remove {row.Name}");
    }

    private async void McpReload_Click(object sender, RoutedEventArgs e) =>
        await RunMcpCommandAsync("/mcp-reload");

    // ============================================================
    // MCP server editor modal (add + edit)
    // ============================================================

    private string? _mcpEditOriginalName;

    private void OpenMcpEditor(string? serverName)
    {
        _mcpEditOriginalName = serverName;
        M_StatusBar.IsOpen = false;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Collapsed;
        McpEditorTestButton.IsEnabled = true;
        McpEditorSaveButton.IsEnabled = true;

        if (serverName != null && _controller.Config.McpServers.TryGetValue(serverName, out var cfg))
        {
            McpEditorTitle.Text = $"Edit MCP server — {serverName}";
            McpEditorSaveButton.Content = "Save & Reconnect";
            M_Name.Text = serverName;
            M_Transport.SelectedIndex = cfg.IsHttp ? 1 : 0;
            M_Command.Text = cfg.Command ?? "";
            M_Args.Text = string.Join(" ", cfg.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            M_Env.Text = string.Join("\n", cfg.Env.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Url.Text = cfg.Url ?? "";
            M_Headers.Text = string.Join("\n", cfg.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
            M_Disabled.IsOn = cfg.Disabled;
        }
        else
        {
            McpEditorTitle.Text = "Add MCP server";
            McpEditorSaveButton.Content = "Save & Connect";
            M_Name.Text = "";
            M_Transport.SelectedIndex = 0;
            M_Command.Text = "";
            M_Args.Text = "";
            M_Env.Text = "";
            M_Url.Text = "";
            M_Headers.Text = "";
            M_Disabled.IsOn = false;
        }

        UpdateMcpTransportPanels();
        McpEditorOverlay.Visibility = Visibility.Visible;
        M_Name.Focus(FocusState.Programmatic);
    }

    private void M_Transport_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateMcpTransportPanels();

    private void UpdateMcpTransportPanels()
    {
        // Guard: fires during InitializeComponent before panels exist.
        if (M_StdioPanel == null || M_HttpPanel == null) return;
        var isHttp = M_Transport.SelectedIndex == 1;
        M_HttpPanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        M_StdioPanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void McpEditorCancel_Click(object sender, RoutedEventArgs e) =>
        McpEditorOverlay.Visibility = Visibility.Collapsed;

    private void ShowMcpEditorError(string message)
    {
        M_TestSpin.IsActive = false;
        M_TestSpin.Visibility = Visibility.Collapsed;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_StatusBar.Severity = InfoBarSeverity.Error;
        M_StatusBar.Title = "Check the form";
        M_StatusBar.Message = message;
        M_StatusBar.IsOpen = true;
    }

    /// <summary>Parses "KEY=value" lines. Returns null (with an error shown) on a bad line.</summary>
    private Dictionary<string, string>? ParseKeyValueLines(string text, string label)
    {
        var dict = new Dictionary<string, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                ShowMcpEditorError($"{label}: '{line}' isn't KEY=value.");
                return null;
            }
            dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    /// <summary>Shared validate-and-build for Test and Save. Shows the error inline and
    /// returns false when the form isn't valid.</summary>
    private bool TryBuildServerFromForm(bool checkNameCollision, out string name, out MandoCode.Models.McpServerConfig server)
    {
        M_StatusBar.IsOpen = false;
        server = new MandoCode.Models.McpServerConfig { Disabled = M_Disabled.IsOn };

        // Lowercased — servers are referenced by name in tool prefixes (mcp_<server>).
        name = M_Name.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) { ShowMcpEditorError("Name cannot be empty."); return false; }
        if (name.Contains(' ')) { ShowMcpEditorError("Name cannot contain spaces."); return false; }
        if (checkNameCollision && _mcpEditOriginalName == null && _controller.Config.McpServers.ContainsKey(name))
        {
            ShowMcpEditorError($"A server named '{name}' already exists — edit it instead, or pick another name.");
            return false;
        }

        if (M_Transport.SelectedIndex == 1)   // http
        {
            var url = M_Url.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                ShowMcpEditorError("URL must be absolute (e.g. https://mcp.example.com/mcp).");
                return false;
            }
            server.Url = url;
            server.Transport = "http";

            var headers = ParseKeyValueLines(M_Headers.Text, "Headers");
            if (headers == null) return false;
            server.Headers = headers;
        }
        else                                   // stdio
        {
            var command = M_Command.Text.Trim();
            if (string.IsNullOrWhiteSpace(command)) { ShowMcpEditorError("Command cannot be empty."); return false; }
            server.Command = command;
            server.Args = ChatController.ParseShellLikeArgs(M_Args.Text.Trim());

            var env = ParseKeyValueLines(M_Env.Text, "Environment variables");
            if (env == null) return false;
            server.Env = env;
        }

        return true;
    }

    private async void McpEditorTest_Click(object sender, RoutedEventArgs e)
    {
        // No collision check — testing an existing name is fine, nothing is written.
        if (!TryBuildServerFromForm(checkNameCollision: false, out var name, out var server)) return;

        M_StatusBar.Severity = InfoBarSeverity.Informational;
        M_StatusBar.Title = "Testing connection…";
        M_StatusBar.Message = "Connecting with these values — nothing is saved, running servers aren't touched.";
        M_StatusBar.IsOpen = true;
        M_TestToolsTable.Visibility = Visibility.Collapsed;
        M_TestSpin.Visibility = Visibility.Visible;
        M_TestSpin.IsActive = true;
        McpEditorTestButton.IsEnabled = false;
        McpEditorSaveButton.IsEnabled = false;

        try
        {
            var result = await Task.Run(() => _controller.TestMcpServerAsync(name, server));

            M_TestSpin.IsActive = false;
            M_TestSpin.Visibility = Visibility.Collapsed;

            if (result.Ok)
            {
                M_StatusBar.Severity = InfoBarSeverity.Success;
                M_StatusBar.Title = $"Connected — {result.Tools.Count} tool(s)";
                M_StatusBar.Message = result.Message;
                if (result.Tools.Count > 0)
                {
                    M_TestTools.ItemsSource = result.Tools
                        .Select(t => new ToolChip { Name = t.Name, Description = t.Description ?? "(no description)" })
                        .ToList();
                    M_TestToolsTable.Visibility = Visibility.Visible;
                }
            }
            else
            {
                M_StatusBar.Severity = InfoBarSeverity.Error;
                M_StatusBar.Title = "Connection failed";
                M_StatusBar.Message = result.Message;
            }
        }
        finally
        {
            M_TestSpin.IsActive = false;
            McpEditorTestButton.IsEnabled = true;
            McpEditorSaveButton.IsEnabled = true;
        }
    }

    private async void McpEditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildServerFromForm(checkNameCollision: true, out var name, out var server)) return;

        McpEditorOverlay.Visibility = Visibility.Collapsed;
        SwitchPage("mcp");
        McpPageStatus.Text = $"Saving '{name}' and connecting…";

        var originalName = _mcpEditOriginalName;
        var (_, message) = await Task.Run(() => _controller.SaveMcpServerAsync(originalName, name, server));
        McpPageStatus.Text = message;
        await RefreshMcpListAsync();
        McpPageStatus.Text = message;
    }

    // ============================================================
    // Project folder + clipboard
    // ============================================================

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps must initialize pickers with the window handle.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _projectRoot.ProjectRoot = folder.Path;
        var fileProvider = App.Services.GetRequiredService<MandoCode.Services.FileAutocompleteProvider>();
        fileProvider.RefreshCache();

        _transcript.Append(_html.Info($"Project root changed to: {folder.Path}"));
        _transcript.Append(_html.Dim("Rebuilding the AI session for the new project…"));

        await Task.Run(async () =>
        {
            var ai = App.Services.GetRequiredService<MandoCode.Services.AIService>();
            await ai.ReinitializeAsync(_controller.Config);
            _transcript.Append(_html.Success("✓ Ready."));
        });
        UpdateStatusBar();
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch { /* a dead link must not crash the app */ }
    }

    private void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
