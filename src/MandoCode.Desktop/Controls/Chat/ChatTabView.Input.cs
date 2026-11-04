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

public sealed partial class ChatTabView
{
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

        var browserContext = CaptureBrowserRequestContext();
        _ = Task.Run(async () =>
        {
            try
            {
                await _controller.SubmitAsync(text, browserContext);
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
        if (e.Key == VirtualKey.Tab
            && SuggestionsPanel.Visibility == Visibility.Visible && _mentionPicker.Fragment is not null)
        {
            e.Handled = true;
            if (!_tabToggleHeld)
            {
                _tabToggleHeld = true;
                _mentionPicker.Toggle();
                UpdateSuggestions();
            }
            return;
        }
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
                    return; // Loading/empty mention panels never submit a partial token.
                }
                SubmitCurrentInput();
            }
        }
        else if (e.Key == VirtualKey.Tab && SuggestionsPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
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
    private void InputBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateSuggestions();
    private void InputBox_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab) _tabToggleHeld = false;
    }

    private void UpdateSuggestions()
    {
        if (_shutDown || _insertingSuggestion) return;
        var text = InputBox.Text;
        var caret = Math.Clamp(InputBox.SelectionStart, 0, text.Length);
        var hasMention = _mentionPicker.Update(text, caret);

        // Slash commands: input starts with '/' and is still a single token.
        if (text.StartsWith('/') && !text.Contains(' '))
        {
            var matches = _controller.GetCommandSuggestions(text);
            if (ShowSuggestions(SuggestMode.Command, 0, caret,
                    matches.Select(m => new CommandSuggestion { Command = m.Command, Description = m.Description })))
                return;
        }

        var tokenStart = caret;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
        if (hasMention && _mentionPicker.Fragment is { } mention)
        {
            _tokenStart = mention.Start; _tokenEnd = mention.End;
            if (_mentionPicker.Target == MentionPickerTarget.Agents)
            {
                CancelFileSuggestionRequest();
                var agents = Session.Agents?.Match(mention.Query, Session.PersistKey) ?? Array.Empty<AgentEntry>();
                ShowSuggestions(SuggestMode.Agent, mention.Start, caret,
                    agents.Select(a => new CommandSuggestion { Command = a.Name, Description = $"{a.Workspace} · {a.FolderLabel}" }), keepOpen: true);
                SetSuggestionStatus(agents.Count == 0 ? "No matching open agents" : "", false);
                return;
            }

            // The provider owns one background scan; closing this popup only drops the UI request.
            // Project/row projection and filtering never enumerate files on the WinUI dispatcher.
            var sameQuery = _suggestMode == SuggestMode.File && _displayedFileQuery == mention.Query
                && _suggestionRoot == Session.ProjectRoot.ProjectRoot;
            if (!sameQuery)
                ShowSuggestions(SuggestMode.File, mention.Start, caret, Array.Empty<CommandSuggestion>(), keepOpen: true);
            _displayedFileQuery = mention.Query;
            _suggestionRoot = Session.ProjectRoot.ProjectRoot;
            SuggestionsTitle.Text = _mentionPicker.Title;
            _ = RefreshFileSuggestionsAsync(mention, _mentionPicker.Revision, _suggestionRoot);
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

    private bool ShowSuggestions(SuggestMode mode, int tokenStart, int tokenEnd, IEnumerable<CommandSuggestion> items, bool keepOpen = false)
    {
        var selected = _suggestMode == mode ? (SuggestionsList.SelectedItem as CommandSuggestion)?.Command : null;
        var selectedIndex = _suggestMode == mode ? SuggestionsList.SelectedIndex : 0;
        var rows = items as IReadOnlyList<CommandSuggestion> ?? items.ToArray();
        if (rows.Count == 0 && !keepOpen) return false;
        _suggestions = rows;
        _suggestMode = mode; _tokenStart = tokenStart; _tokenEnd = tokenEnd;
        SuggestionsTitle.Text = mode is SuggestMode.File or SuggestMode.Agent ? _mentionPicker.Title
            : mode == SuggestMode.Emoji ? "Emojis" : "Commands";
        SetSuggestionStatus("", false);
        SuggestionsList.ItemsSource = rows;
        SuggestionsPanel.Visibility = Visibility.Visible;
        var retained = -1;
        if (selected is not null)
            for (var i = 0; i < rows.Count; i++) if (rows[i].Command == selected) { retained = i; break; }
        SuggestionsList.SelectedIndex = rows.Count == 0 ? -1 : retained >= 0 ? retained : Math.Clamp(selectedIndex, 0, rows.Count - 1);
        if (SuggestionsList.SelectedItem is { } item) SuggestionsList.ScrollIntoView(item);
        return true;
    }

    private void SetSuggestionStatus(string text, bool loading)
    {
        SuggestionsStatusText.Text = text;
        SuggestionsLoading.IsActive = loading;
        SuggestionsLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelFileSuggestionRequest()
    {
        _fileSuggestionVersion++;
        _fileSuggestionRequest?.Cancel(); _fileSuggestionRequest?.Dispose();
        _fileSuggestionRequest = null;
    }

    private async Task RefreshFileSuggestionsAsync(MentionFragment mention, long revision, string root)
    {
        CancelFileSuggestionRequest();
        var version = _fileSuggestionVersion;
        _fileSuggestionRequest = new();
        var cancellation = _fileSuggestionRequest.Token;
        if (_suggestions.Count == 0) SetSuggestionStatus("Loading files and folders…", true);
        try
        {
            var result = await _fileLookup.GetAsync(mention.Query, cancellation);
            if (_shutDown || cancellation.IsCancellationRequested || version != _fileSuggestionVersion
                || revision != _mentionPicker.Revision || _mentionPicker.Target != MentionPickerTarget.Files
                || _mentionPicker.Fragment != mention || Session.ProjectRoot.ProjectRoot != root) return;
            if (result.Items.Length == 0 && !result.Loading && mention.Query.EndsWith('/'))
            {
                // A quoted folder prefix stays open while drilling. Finalize it when
                // there is nothing left to select, so sending it still references a folder.
                var reference = MandoCode.Services.FileReferenceToken.Format(mention.Query);
                _insertingSuggestion = true;
                try
                {
                    InputBox.Text = InputBox.Text[..mention.Start] + reference + InputBox.Text[mention.End..];
                    InputBox.SelectionStart = mention.Start + reference.Length;
                }
                finally { _insertingSuggestion = false; }
                HideSuggestions(); // The selected folder remains in the prompt; drill-down ends here.
                return;
            }
            if (!ReferenceEquals(_suggestions, result.Items))
                ShowSuggestions(SuggestMode.File, mention.Start, mention.End, result.Items, keepOpen: true);
            SetSuggestionStatus(result.Loading ? result.Items.Length == 0 ? "Loading files and folders…"
                : result.Indexing ? "Indexing subfolders…" : "Filtering files…"
                : result.Items.Length == 0 ? "No matching files or folders" : "", result.Loading);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_shutDown && version == _fileSuggestionVersion) SetSuggestionStatus("Files could not be loaded", false);
        }
    }

    private void OnFileSuggestionsChanged()
    {
        if (Interlocked.Exchange(ref _queuedFileRefresh, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queuedFileRefresh, 0);
            if (!_shutDown && _mentionPicker.Fragment is not null && _mentionPicker.Target == MentionPickerTarget.Files)
                UpdateSuggestions();
        })) Interlocked.Exchange(ref _queuedFileRefresh, 0);
    }
    private void OnAgentSuggestionsChanged() => OnUi(() =>
    {
        if (!_shutDown && _mentionPicker.Fragment is not null && _mentionPicker.Target == MentionPickerTarget.Agents)
            UpdateSuggestions();
    });

    private void SuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandSuggestion s) AcceptSuggestion(s);
    }

    private void AcceptSuggestion(CommandSuggestion s)
    {
        _insertingSuggestion = true;
        try
        {
            if (_suggestMode == SuggestMode.Agent)
            {
                var text = InputBox.Text;
                var start = Math.Min(_tokenStart, text.Length);
                var end = Math.Min(_tokenEnd, text.Length);

                // Always closes with a space: a callsign is a whole token, so there is nothing to drill
                // into the way a folder has.
                if (Session.Agents?.Resolve(s.Command) is not { } agent || agent.Key == Session.PersistKey)
                    return;
                var replacement = MandoCode.Services.FileReferenceToken.Format(s.Command) + " ";
                InputBox.Text = text[..start] + replacement + text[end..];
                InputBox.SelectionStart = start + replacement.Length;
                HideSuggestions();
            }
            else if (_suggestMode == SuggestMode.File)
            {
                var text = InputBox.Text;
                var start = Math.Min(_tokenStart, text.Length);
                var end = Math.Min(_tokenEnd, text.Length);

                // Replace the @token with the picked path. Directories keep the caret hot
                // (no trailing space) so the reopened popup shows their contents; files
                // close the token with a space.
                var isFolder = s.Command.EndsWith('/');
                var replacement = PromptMentionPicker.FileReference(s.Command, Session.Agents?.Resolve(s.Command) is not null)
                    + (isFolder ? "" : " ");
                if (isFolder && replacement.StartsWith("@\"")) replacement = replacement[..^1];
                InputBox.Text = text[..start] + replacement + text[end..];
                InputBox.SelectionStart = start + replacement.Length;

                // Setting .Text resets the caret to 0 BEFORE the line above restores it, and
                // TextChanged runs in that window — it sees no token at caret 0 and hides the
                // popup. Recompute now that the caret is where the user expects it:
                // folder → drilled listing reopens; file → token ended with a space, stays hidden.
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
        }
        finally { _insertingSuggestion = false; }
        UpdateSuggestions();
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
        CancelFileSuggestionRequest();
        _mentionPicker.Dismiss();
        _suggestMode = SuggestMode.None;
        _displayedFileQuery = null;
        SuggestionsPanel.Visibility = Visibility.Collapsed;
        _suggestions = Array.Empty<CommandSuggestion>();
        SuggestionsList.ItemsSource = _suggestions;
        SetSuggestionStatus("", false);
    }
}
