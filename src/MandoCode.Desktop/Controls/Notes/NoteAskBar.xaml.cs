using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// The prompt bar at the bottom of the Notes panel. Deliberately dumb: it collects a question, shows a
/// streamed reply, and raises events. It owns no model, no thread, and no note — the window drives it,
/// because which surface is showing decides what the question is even about.
///
/// The reply always gets the same three offers (Insert / Replace / Dismiss) rather than the bar trying
/// to classify whether a reply is prose or a proposed rewrite. Small local models tag their own output
/// unreliably, and guessing wrong in either direction is worse than letting the person who asked
/// decide: the note is only ever written by an explicit press.
/// </summary>
public sealed partial class NoteAskBar : UserControl
{
    /// <summary>A question was submitted (Enter or the send button).</summary>
    public event Action<string>? Submitted;

    /// <summary>The send button was pressed while a reply was streaming.</summary>
    public event Action? CancelRequested;

    /// <summary>Insert the reply into the open note at the cursor.</summary>
    public event Action<string>? InsertRequested;

    /// <summary>Replace the open note's whole body with the reply.</summary>
    public event Action<string>? ReplaceRequested;

    /// <summary>A model was chosen from the chip's flyout.</summary>
    public event Action<string>? ModelChanged;

    private readonly System.Text.StringBuilder _reply = new();
    private bool _busy;

    public NoteAskBar()
    {
        InitializeComponent();
    }

    /// <summary>The reply as it stands — what Insert/Replace would apply.</summary>
    public string Reply => _reply.ToString().Trim();

    public bool IsBusy => _busy;

    /// <summary>Whether the note-writing offers are available. False on the list view, where there's no
    /// open note to write into and the reply is just an answer.</summary>
    public bool AllowNoteEdits { get; set; }

    public void FocusPrompt() => PromptBox.Focus(FocusState.Programmatic);

    /// <summary>Switches the bar between its two jobs. Called whenever the panel changes surface.</summary>
    public void SetMode(bool noteOpen, string? noteTitle)
    {
        AllowNoteEdits = noteOpen;
        PromptBox.PlaceholderText = noteOpen
            ? $"Ask about “{noteTitle}” — or tell it what to write"
            : "Ask about all your notes…";
        InsertButton.Visibility = noteOpen ? Visibility.Visible : Visibility.Collapsed;
        ReplaceButton.Visibility = noteOpen ? Visibility.Visible : Visibility.Collapsed;
        InsertLabel.Text = "Insert";
    }

    /// <summary>Label under the prompt saying what the model was actually given — the honest version of
    /// a capped context ("12 notes listed · 3 read in full"), so a partial read never looks total.</summary>
    public void SetScopeNote(string text) => ScopeText.Text = text;

    /// <summary>Notes that a selection exists, so Insert reads as replacing it rather than adding.</summary>
    public void SetHasSelection(bool hasSelection) =>
        InsertLabel.Text = hasSelection ? "Replace selection" : "Insert";

    // ---- model chip ----

    /// <summary>Names the model that WOULD answer, before the model list has come back from Ollama.
    /// Without this the chip reads "no model" for the first second or two of every panel open — which
    /// says the bar won't work, while it in fact answers fine on the configured default.</summary>
    public void SetModelLabel(string? model) =>
        ModelText.Text = string.IsNullOrWhiteSpace(model) ? "no model" : model;

    public void SetModels(IReadOnlyList<string> models, string? current)
    {
        SetModelLabel(current);

        var flyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
        };
        if (models.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No models found — is Ollama running?",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (var model in models)
            {
                var item = new MenuFlyoutItem { Text = model };
                var captured = model;
                item.Click += (_, _) =>
                {
                    ModelText.Text = captured;
                    ModelChanged?.Invoke(captured);
                };
                flyout.Items.Add(item);
            }
        }
        ModelButton.Flyout = flyout;
    }

    // ---- reply lifecycle (driven by the window) ----

    /// <summary>Opens an empty reply and puts the bar into its streaming state.</summary>
    public void BeginReply(string label)
    {
        _reply.Clear();
        ReplyText.Text = "";
        ReplyLabel.Text = label;
        ReplyBox.Visibility = Visibility.Visible;
        ActionRow.Visibility = Visibility.Collapsed;
        ReplyRing.IsActive = true;
        ReplyRing.Visibility = Visibility.Visible;
        SetBusy(true);
    }

    /// <summary>Appends a streamed chunk. Must be called on the UI thread.</summary>
    public void AppendDelta(string text)
    {
        if (text.Length == 0) return;
        _reply.Append(text);
        ReplyText.Text = _reply.ToString();
        ReplyScroller.ChangeView(null, ReplyScroller.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>Closes a reply. <paramref name="error"/> replaces the body when the call failed — a
    /// silent empty strip would read as "the model had nothing to say".</summary>
    public void EndReply(string? error = null, string? label = null)
    {
        SetBusy(false);
        ReplyRing.IsActive = false;
        ReplyRing.Visibility = Visibility.Collapsed;

        if (error != null)
        {
            ReplyLabel.Text = "couldn't answer";
            ReplyText.Text = error;
            ActionRow.Visibility = Visibility.Collapsed;
            return;
        }

        if (label != null) ReplyLabel.Text = label;

        var empty = Reply.Length == 0;
        if (empty) ReplyText.Text = "(no answer came back)";
        ActionRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Clears the reply — on switching notes, or on Dismiss.</summary>
    public void ClearReply()
    {
        _reply.Clear();
        ReplyText.Text = "";
        ReplyBox.Visibility = Visibility.Collapsed;
        ActionRow.Visibility = Visibility.Collapsed;
        ReplyRing.IsActive = false;
        ReplyRing.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // The send button doubles as stop: one control, and there's only ever one request in flight.
        SendIcon.Glyph = busy ? "" : "";
        ToolTipService.SetToolTip(SendButton, busy ? "Stop" : "Ask (Enter)");
        PromptBox.IsEnabled = !busy;
    }

    // ---- input ----

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Submit();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) CancelRequested?.Invoke();
        else Submit();
    }

    private void Submit()
    {
        if (_busy) return;
        var question = PromptBox.Text.Trim();
        if (question.Length == 0) return;
        PromptBox.Text = "";
        Submitted?.Invoke(question);
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (Reply.Length > 0) InsertRequested?.Invoke(Reply);
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (Reply.Length > 0) ReplaceRequested?.Invoke(Reply);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Reply.Length == 0) return;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(Reply);
        Clipboard.SetContent(package);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => ClearReply();
}
