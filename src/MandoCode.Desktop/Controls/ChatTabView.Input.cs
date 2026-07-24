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
}
