using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// One open note: a plain TextBox over one file, with autosave, rename-in-place, and the conflict
/// handling a file-backed editor can't skip.
///
/// <b>Autosave.</b> There is no Save button. The point of a jot pad is writing a thought down without
/// ceremony, and a jot you have to remember to save is a jot you lose. Writes land
/// <see cref="AutosaveDebounceMs"/>ms after you stop typing, plus on Ctrl+S, on leaving the note, on
/// closing the panel, and on closing the window.
///
/// <b>This editor is the only thing that writes note content — but not the only thing that writes the
/// FILE.</b> These are plain files in <c>~/.mandocode/notes</c>; Notepad, VS Code, a sync client, or
/// git can all change one under you. So every write is tracked against <see cref="_lastSavedText"/> —
/// what we believe is on disk — and a FileSystemWatcher compares against it: identical means the write
/// was ours, changed-while-clean is adopted silently, and changed-while-you-were-typing is a conflict
/// the user resolves. No path here silently discards typing.
///
/// The note assistant is NOT a writer: it has no file tools at all (see <see cref="NoteAssistant"/>).
/// Its output reaches a note only through <see cref="InsertAtCursor"/> or <see cref="ReplaceBody"/>,
/// each of which is a button the user pressed.
/// </summary>
public sealed partial class NoteEditorPane : UserControl
{
    /// <summary>Idle time before an autosave fires. Long enough not to write on every keystroke, short
    /// enough that "did that stick?" is never a real question.</summary>
    private const int AutosaveDebounceMs = 1200;

    // Note-typed members are INTERNAL on purpose: the XAML markup compiler walks a UserControl's public
    // properties and emits `new T()` activators for their types, which fails on NoteEntry's required
    // members. Internal keeps it out of that walk, and MainWindow is the only consumer.

    /// <summary>Set once by the window. The editor writes note CONTENT; the store owns the file
    /// lifecycle (create/rename/delete) it delegates to.</summary>
    internal NoteStore? Store { get; set; }

    internal NoteEntry? Current { get; private set; }

    public event Action? BackRequested;

    /// <summary>Raised once the note file is gone, so the panel can drop back to the list.</summary>
    internal event Action<NoteEntry>? Deleted;

    /// <summary>Raised after a successful rename, carrying the note at its new path.</summary>
    internal event Action<NoteEntry>? Renamed;

    /// <summary>Raised whenever the note's bytes on disk change (our save, or an adopted external
    /// edit) so the card behind the editor stays truthful.</summary>
    internal event Action<NoteEntry>? Stored;

    // Fully qualified: Windows.System (imported here for VirtualKey) has a DispatcherQueue too.
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _autosave = new();

    /// <summary>What's on disk, in EDITOR form (see <see cref="NoteText"/> — a WinUI TextBox holds
    /// newlines as bare CR whatever the file uses). The watcher's yardstick for "was that change
    /// mine?" — content comparison rather than a timing window, which is the only version of this that
    /// can't misfire under a slow disk.</summary>
    private string _lastSavedText = "";

    /// <summary>The note's own line ending, detected on load and restored on save.</summary>
    private string _newline = Environment.NewLine;

    private bool _dirty;
    private bool _suppressDirty;

    public NoteEditorPane()
    {
        InitializeComponent();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _autosave.Interval = TimeSpan.FromMilliseconds(AutosaveDebounceMs);
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveNow(); };
    }

    // ---- what the assistant and the ask bar need ----

    /// <summary>The live buffer — what the assistant is given, so it always sees the note as it is now,
    /// including keystrokes not yet autosaved.</summary>
    public string Body => Editor.Text;

    /// <summary>The selected passage, or "" — lets a prompt act on a highlighted paragraph.</summary>
    public string SelectionText => Editor.SelectedText ?? "";

    /// <summary>Puts assistant output into the note at the cursor, replacing the selection if there is
    /// one. Goes through the normal dirty/autosave path, so it saves like typing does — and is undoable
    /// in the TextBox like anything else.</summary>
    public void InsertAtCursor(string text)
    {
        if (Current == null || Editor.IsReadOnly || text.Length == 0) return;

        var body = Editor.Text;
        var start = Math.Clamp(Editor.SelectionStart, 0, body.Length);
        var length = Math.Clamp(Editor.SelectionLength, 0, body.Length - start);

        Editor.Text = body[..start] + text + body[(start + length)..];
        Editor.Select(start + text.Length, 0);
        Editor.Focus(FocusState.Programmatic);

        SetDirty(true);
        _autosave.Stop();
        _autosave.Start();
    }

    /// <summary>Replaces the whole note with assistant output. Same path as typing it by hand.</summary>
    public void ReplaceBody(string text)
    {
        if (Current == null || Editor.IsReadOnly) return;

        Editor.Text = text;
        Editor.Select(Editor.Text.Length, 0);
        Editor.Focus(FocusState.Programmatic);

        SetDirty(true);
        _autosave.Stop();
        _autosave.Start();
    }

    // ---- opening / closing ----

    /// <summary>Loads a note, replacing whatever was open (which is saved first — switching notes must
    /// never be a way to lose one).</summary>
    internal void Open(NoteEntry note)
    {
        CloseCurrent();

        string text;
        try
        {
            text = File.Exists(note.Path) ? File.ReadAllText(note.Path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read it as empty and DON'T let autosave overwrite a file we couldn't read.
            Current = note;
            LoadIntoEditor("");
            RefreshHeader();
            SetStatus($"Couldn't open this note — {ex.Message}");
            Editor.IsReadOnly = true;
            return;
        }

        Current = note;
        Editor.IsReadOnly = false;

        LoadIntoEditor(text);
        _dirty = false;
        ClearConflict();

        StartWatching(note.Path);
        RefreshHeader();
        SetStatus(text.Length == 0 ? "New note" : "Opened");
        Editor.Select(Editor.Text.Length, 0);   // caret at the end — you're here to add, not re-read
    }

    /// <summary>
    /// Puts file text into the TextBox and re-reads it back as the disk yardstick. Reading it BACK is
    /// the point: the control rewrites newlines on assignment, so <c>Editor.Text</c> is generally not
    /// the string we just handed it — and comparing our original against the control's version is what
    /// made every open look like an edit (and autosave an untouched note).
    /// </summary>
    private void LoadIntoEditor(string fileText)
    {
        _newline = NoteText.DetectNewline(fileText);

        _suppressDirty = true;
        Editor.Text = fileText;
        _suppressDirty = false;

        _lastSavedText = Editor.Text;
    }

    /// <summary>Editor text → file text, in this note's own line ending (see <see cref="NoteText"/>).</summary>
    private string ToFileText(string editorText) => NoteText.ToFileText(editorText, _newline);

    public void FocusEditor() => Editor.Focus(FocusState.Programmatic);

    /// <summary>Commits any debounced edit immediately. Called when the panel closes, the window
    /// closes, or another note is opened.</summary>
    public void FlushPendingSave()
    {
        _autosave.Stop();
        SaveNow();
    }

    /// <summary>Saves and stops watching, without clearing <see cref="Current"/> — the window can still
    /// ask what was open.</summary>
    public void Shutdown()
    {
        FlushPendingSave();
        StopWatching();
    }

    private void CloseCurrent()
    {
        if (Current == null) return;
        Shutdown();
        Current = null;
    }

    /// <summary>Leaves the note entirely (back to the list).</summary>
    public void Close()
    {
        CloseCurrent();
        ClearConflict();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // ---- editing / autosave ----

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirty || Current == null) return;

        // WinUI raises TextChanged asynchronously, so the event for our own programmatic load can
        // arrive AFTER _suppressDirty is cleared. Content is the authority: text equal to what's on
        // disk is not an edit, whenever the notification shows up.
        if (Editor.Text == _lastSavedText)
        {
            if (_dirty) SetDirty(false);
            _autosave.Stop();
            return;
        }

        SetDirty(true);
        _autosave.Stop();
        _autosave.Start();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+S saves now. A plain KeyDown rather than a KeyboardAccelerator: accelerators on this
        // window have a history of native-crashing on non-alphanumeric keys, and this is correctly
        // scoped to the editor anyway.
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.S)
        {
            _autosave.Stop();
            SaveNow();
            e.Handled = true;
        }
    }

    private void SetDirty(bool value)
    {
        _dirty = value;
        DirtyDot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value) SetStatus("Unsaved — autosaving…");
    }

    /// <summary>
    /// Writes the buffer to the note file. A failed write keeps the dirty flag and says so — the text
    /// stays in the TextBox and the next keystroke or Ctrl+S retries; pretending it saved is the one
    /// outcome that loses a note.
    /// </summary>
    /// <param name="force">Write even if nothing looks changed. Used by the conflict resolutions: there
    /// the file holds someone ELSE's version, so "keep what I typed" must overwrite it even when the
    /// buffer matches what this editor last wrote.</param>
    private void SaveNow(bool force = false)
    {
        if (Current == null || Editor.IsReadOnly) return;
        if (!force && !_dirty) return;

        // Deleted out from under us: writing would resurrect it silently. The conflict bar's
        // "Save it back" is the deliberate (forced) version of that.
        if (_conflict == Conflict.DeletedOnDisk && !force) return;

        var text = Editor.Text;

        // Nothing actually differs from disk (an undo back to the original, say): writing would only
        // bump the modified time and reorder the list for no reason.
        if (!force && text == _lastSavedText)
        {
            SetDirty(false);
            return;
        }

        var fileText = ToFileText(text);
        try
        {
            File.WriteAllText(Current.Path, fileText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save — {ex.Message}");
            return;
        }

        _lastSavedText = text;
        SetDirty(false);
        SetStatus($"Saved {DateTime.Now:h:mm tt}");
        NotifyStored();
    }

    /// <summary>Re-reads the note's metadata and tells the panel, so the card behind the editor shows
    /// the new size, preview, and timestamp.</summary>
    private void NotifyStored()
    {
        if (Current == null || Store == null) return;
        var refreshed = Store.Reread(Current);
        if (refreshed == null) return;
        Current = refreshed;
        RefreshHeader();
        Stored?.Invoke(refreshed);
    }

    // ---- header / status ----

    private void RefreshHeader()
    {
        if (Current == null) return;
        TitleText.Text = Current.Title;
        GroupText.Text = Current.GroupLabel;
        SizeText.Text = Current.SizeLabel;
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
