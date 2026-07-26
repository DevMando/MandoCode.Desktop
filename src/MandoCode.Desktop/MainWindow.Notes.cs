using System.Text;
using MandoCode.Desktop.Services;
using MandoCode.Services;   // OllamaSetupHelper — lists models without needing an agent
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    // ============================================================
    // Notes panel — the jot pad. The third docked panel, sharing the column with Snapshots and History.
    //
    // Notes are APP-WIDE, like both of those: plain text files under ~\.mandocode\notes. A note is
    // something you want to write down now, which is often between projects or before an agent is even
    // open, so nothing here requires one. What survives of "which project was this about" is a plain
    // subfolder: a new note is filed under the active agent's folder name when there is one.
    //
    // Two states in one column — the grouped browse list, and the editor that replaces it while a note
    // is open — with the ask bar pinned below both. The bar's question means different things in each
    // state (this note / the whole pad), which is all the "mode" there is.
    // ============================================================

    private readonly NoteStore _notes = new();

    /// <summary>Group headings folded shut. Persisted, like the other panels'.</summary>
    private readonly HashSet<string> _collapsedNoteGroups = new();

    /// <summary>The note open when the panel last closed, so reopening lands back in it.</summary>
    private string? _lastNotePath;

    /// <summary>Model the note assistant answers with. Persisted; falls back to the app-wide default.</summary>
    private string? _noteModel;

    private string _noteFilter = "";

    /// <summary>Last discovery result. The panel renders from this, so a keystroke in the search box
    /// filters in memory instead of walking the pad again.</summary>
    private List<NoteEntry> _noteCache = new();

    private bool _notesScanned;
    private bool _lastNoteRestored;
    private bool _noteModelsFetched;

    // In-memory conversation with the assistant: one thread for the open note, one for the pad. Notes
    // aren't conversations — these are just enough for "shorter" or "now the second one" to mean
    // something, and they're deliberately not persisted.
    private readonly List<NoteAssistant.Turn> _noteThread = new();
    private readonly List<NoteAssistant.Turn> _padThread = new();
    private string? _noteThreadFor;
    private const int MaxThreadTurns = 12;

    private CancellationTokenSource? _noteAskCts;

    /// <summary>Subscribes to the editor and the ask bar. Called once from the constructor — both are
    /// XAML children that live as long as the window, so these never need detaching.</summary>
    private void WireNotesPanel()
    {
        NoteEditor.BackRequested += ShowNoteBrowse;

        NoteEditor.Stored += note =>
        {
            UpsertNoteCache(note);
            _lastNotePath = note.Path;
        };

        NoteEditor.Renamed += note =>
        {
            _lastNotePath = note.Path;
            SavePanelState();
            _ = RefreshNotesAsync();   // the old path is gone from disk; rescan rather than patch
        };

        NoteEditor.Deleted += _ => ShowNoteBrowse();

        NoteAsk.Submitted += question => _ = AskNotesAsync(question);
        NoteAsk.CancelRequested += () => _noteAskCts?.Cancel();

        // The two ways assistant output can reach a note — both a button the user pressed.
        NoteAsk.InsertRequested += text =>
        {
            NoteEditor.InsertAtCursor(text);
            NoteAsk.ClearReply();
        };
        NoteAsk.ReplaceRequested += text =>
        {
            NoteEditor.ReplaceBody(text);
            NoteAsk.ClearReply();
        };

        NoteAsk.ModelChanged += model =>
        {
            _noteModel = model;
            SavePanelState();
        };
    }

    // ---- panel open/close ----

    private void NavNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesPanelOpen) CloseLeftPanel();
        else OpenNotes();
    }

    private void CloseNotes_Click(object sender, RoutedEventArgs e) => CloseLeftPanel();

    private void OpenNotes()
    {
        PopulateNotes();            // instant, from the cache
        NoteAsk.SetModelLabel(CurrentNoteModel());   // name the model now; the list fills in behind it
        ShowLeftPanel(LeftPanel.Notes);
        _ = RefreshNotesAsync();    // then the truth, from disk
        _ = RefreshNoteModelsAsync();
    }

    /// <summary>Rescans the pad off the UI thread, then repaints. Fired on panel open and after any
    /// create/rename/delete — never on a keystroke.</summary>
    private async Task RefreshNotesAsync()
    {
        List<NoteEntry> found;
        try
        {
            found = (await Task.Run(() => _notes.Discover())).ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write("notes-discovery", ex);
            return;
        }

        _noteCache = found;
        _notesScanned = true;

        if (!NotesPanelOpen) return;
        PopulateNotes();
        RestoreLastNoteOnce();
    }

    /// <summary>Fills the assistant's model picker. Best-effort and once per launch: the list comes from
    /// Ollama, which may not be running — that's what the picker's own empty state says.</summary>
    private async Task RefreshNoteModelsAsync()
    {
        if (_noteModelsFetched) return;
        _noteModelsFetched = true;

        var endpoint = _configs.Defaults.OllamaEndpoint;
        OllamaSetupHelper.ListModelsResult result;
        try
        {
            result = await OllamaSetupHelper.ListModelsWithStatusAsync(endpoint);
        }
        catch
        {
            NoteAsk.SetModels(Array.Empty<string>(), CurrentNoteModel());
            return;
        }

        var models = result.Ok ? result.Models : new List<string>();
        // A remembered model that's since been removed shouldn't silently answer as something else.
        if (_noteModel != null && !models.Contains(_noteModel, StringComparer.OrdinalIgnoreCase))
            _noteModel = null;

        NoteAsk.SetModels(models, CurrentNoteModel());
    }

    /// <summary>The assistant's model: the user's pick for this panel, else the app-wide default.</summary>
    private string? CurrentNoteModel()
    {
        if (!string.IsNullOrWhiteSpace(_noteModel)) return _noteModel;
        var fallback = _configs.Defaults.GetEffectiveModelName();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>Reopens the note that was open when the panel last closed — once per launch, and only if
    /// it's still on disk. Notes are come-back-to things; landing on the list every launch would mean
    /// re-finding your note every time.</summary>
    private void RestoreLastNoteOnce()
    {
        if (_lastNoteRestored) return;
        _lastNoteRestored = true;

        if (_lastNotePath == null || NoteEditor.Current != null) return;
        var note = _noteCache.FirstOrDefault(n =>
            string.Equals(n.Path, _lastNotePath, StringComparison.OrdinalIgnoreCase));
        if (note != null) OpenNote(note);
    }

    // ---- browse list ----

    /// <summary>
    /// Paints the panel for its current state. Both states share a grid row, so this is also what
    /// switches between them: an open note means editor, otherwise the grouped list. Safe to call any
    /// time — everything is derived from state.
    /// </summary>
    private void PopulateNotes()
    {
        var editing = NoteEditor.Current != null;

        NotesBlurb.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        NoteEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        NoteAsk.SetMode(editing, NoteEditor.Current?.Title);
        RefreshNoteAskScope();

        if (editing)
        {
            NotesSearch.Visibility = Visibility.Collapsed;
            NotesScroller.Visibility = Visibility.Collapsed;
            NotesEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var padEmpty = _noteCache.Count == 0;
        NotesSearch.Visibility = padEmpty ? Visibility.Collapsed : Visibility.Visible;

        var q = _noteFilter;
        var rows = _noteCache
            .Where(n => NoteStore.Matches(n, q))
            .Select(n => new NoteRow
            {
                Note = n,
                MatchSnippet = string.IsNullOrEmpty(q) ? "" : NoteStore.MatchSnippet(n, q) ?? "",
            })
            .ToList();

        // Grouped by folder, freshest group first, newest note first inside each — the same ordering as
        // Snapshots and History, so the three panels behave identically.
        var groups = rows
            .GroupBy(r => r.Note.GroupLabel)
            .OrderByDescending(g => g.Max(r => r.Note.ModifiedAt))
            .Select(g => new NoteGroup(g.Key, g.OrderByDescending(r => r.Note.ModifiedAt))
            {
                IsExpanded = !_collapsedNoteGroups.Contains(g.Key),
            })
            .ToList();

        NotesList.ItemsSource = groups;

        var nothingToShow = groups.Count == 0;
        NotesEmpty.Text = !_notesScanned
            ? "Looking for notes…"
            : padEmpty
                ? "No notes yet. Hit New and start typing — it's saved as you go, into "
                  + $"{_notes.Root}. With an agent open, the note is filed under that project's folder."
                : $"No notes match “{q}”.";
        NotesEmpty.Visibility = nothingToShow ? Visibility.Visible : Visibility.Collapsed;
        NotesScroller.Visibility = nothingToShow ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Tells the user what the assistant would actually be given. A capped read that looks total
    /// is the one thing a "ask about all my notes" box must not do.</summary>
    private void RefreshNoteAskScope()
    {
        if (NoteEditor.Current != null)
        {
            NoteAsk.SetScopeNote("reads this note as it is right now");
            return;
        }

        var indexed = Math.Min(_noteCache.Count, NoteAssistant.MaxIndexedNotes);
        var bodies = NoteAssistant.CountBodiesSent(NotesForFullRead());

        NoteAsk.SetScopeNote(_noteCache.Count == 0
            ? "nothing written yet"
            : $"{indexed} note{(indexed == 1 ? "" : "s")} listed · {bodies} read in full"
              + (string.IsNullOrEmpty(_noteFilter) ? "" : $" (matching “{_noteFilter}”)"));
    }

    /// <summary>
    /// Which notes get their full text sent with a pad-wide question: whatever the search box is
    /// matching, else the most recently touched. Search is the user saying "these are the ones I mean",
    /// which is a better selector than anything the app could guess — and it keeps the request bounded.
    /// </summary>
    private List<NoteEntry> NotesForFullRead()
    {
        var pool = string.IsNullOrEmpty(_noteFilter)
            ? _noteCache
            : _noteCache.Where(n => NoteStore.Matches(n, _noteFilter)).ToList();

        return pool.Take(NoteAssistant.MaxFullNotes).ToList();
    }

    private void NotesSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only the user typing — not the programmatic Text changes a repopulate can cause.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _noteFilter = sender.Text?.Trim() ?? "";
        PopulateNotes();
    }

    // The group object is kept in sync as well as the set, so a recycled ListView container re-reads the
    // correct state from its OneTime IsExpanded binding (same as the other two panels).
    private void NoteGroup_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.Tag is not NoteGroup g) return;
        g.IsExpanded = true;
        _collapsedNoteGroups.Remove(g.Project);
        SavePanelState();
    }

    private void NoteGroup_Collapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (sender.Tag is not NoteGroup g) return;
        g.IsExpanded = false;
        _collapsedNoteGroups.Add(g.Project);
        SavePanelState();
    }

    /// <summary>Replaces one note in the cache (matched by path) so the list repaints without a rescan.
    /// Adds it if it's new.</summary>
    private void UpsertNoteCache(NoteEntry note)
    {
        var i = _noteCache.FindIndex(n => string.Equals(n.Path, note.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _noteCache[i] = note;
        else _noteCache.Insert(0, note);
    }

    // ---- open / create / delete ----

    private void OpenNote(NoteEntry note)
    {
        // A different note is a different subject: the assistant's thread and any pending reply from the
        // previous note would be answering about text that's no longer on screen.
        if (!string.Equals(_noteThreadFor, note.Path, StringComparison.OrdinalIgnoreCase))
        {
            _noteAskCts?.Cancel();
            _noteThread.Clear();
            _noteThreadFor = note.Path;
            NoteAsk.ClearReply();
        }

        NoteEditor.Open(note);
        _lastNotePath = note.Path;
        SavePanelState();
        PopulateNotes();          // switches the panel into edit state
        NoteEditor.FocusEditor();
    }

    private void ShowNoteBrowse()
    {
        _noteAskCts?.Cancel();    // the reply strip is about to be cleared; stop streaming into it
        NoteEditor.Close();       // saves and stops watching
        _lastNotePath = null;
        _noteThread.Clear();
        _noteThreadFor = null;
        NoteAsk.ClearReply();
        SavePanelState();
        PopulateNotes();
        _ = RefreshNotesAsync();
    }

    private async void NoteOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NoteEntry note) return;

        // The card may be seconds stale — the note could have been edited or deleted outside the app.
        var fresh = _notes.Reread(note);
        if (fresh == null)
        {
            await SayAsync("That note is gone", $"“{note.FileName}” is no longer on disk. The list has been refreshed.");
            _ = RefreshNotesAsync();
            return;
        }
        OpenNote(fresh);
    }

    private async void NoteNew_Click(object sender, RoutedEventArgs e)
    {
        // Filed under the folder you're working in, when there is one. No agent is fine — that's the
        // point of a jot pad, and the note simply lands unfiled.
        var root = _sessions.Active?.ProjectRoot.ProjectRoot;
        var group = string.IsNullOrWhiteSpace(root) ? null : ProjectDisplay.ProjectLabel(root);

        NoteEntry note;
        try
        {
            note = _notes.Create(group: group);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SayAsync("Couldn't create the note", $"Writing to {_notes.Root} failed — {ex.Message}");
            return;
        }

        UpsertNoteCache(note);
        OpenNote(note);
        _ = RefreshNotesAsync();
    }

    private async void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NoteEntry note) return;

        var dialog = new ContentDialog
        {
            Title = "Delete note",
            Content = $"Delete “{note.FileName}”? The file is removed from disk. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (!_notes.Delete(note))
        {
            await SayAsync("Couldn't delete that note", $"“{note.FileName}” may be open in another program.");
            return;
        }

        _noteCache.RemoveAll(n => string.Equals(n.Path, note.Path, StringComparison.OrdinalIgnoreCase));
        PopulateNotes();
        _ = RefreshNotesAsync();
    }

    // ---- the assistant ----

    /// <summary>
    /// Asks about the open note, or about the pad when you're on the list. Streams into the bar's reply
    /// strip; the reply reaches a note only if the user then presses Insert or Replace.
    ///
    /// The note's text is taken from the live editor buffer rather than from disk, so the model sees what
    /// you see — including the last few seconds of typing that autosave hasn't written yet.
    /// </summary>
    private async Task AskNotesAsync(string question)
    {
        var model = CurrentNoteModel();
        if (string.IsNullOrWhiteSpace(model))
        {
            NoteAsk.BeginReply("no model");
            NoteAsk.EndReply(error: "No model selected. Pick one from the chip below the prompt — "
                                  + "or start Ollama (ollama serve) if the list is empty.");
            return;
        }

        var endpoint = _configs.Defaults.OllamaEndpoint;
        var note = NoteEditor.Current;
        var onNote = note != null;
        var thread = onNote ? _noteThread : _padThread;

        var selection = onNote ? NoteEditor.SelectionText : "";
        NoteAsk.SetHasSelection(selection.Length > 0);

        NoteAsk.BeginReply(onNote
            ? $"{model} · {(selection.Length > 0 ? "your selection" : note!.Title)}"
            : $"{model} · your notes");

        _noteAskCts?.Cancel();
        _noteAskCts = new CancellationTokenSource();
        var ct = _noteAskCts.Token;

        // Deltas arrive on a background thread, possibly hundreds per second from a small model, and
        // every repaint re-lays-out the entire reply. Posting per token starves the UI thread, and
        // even coalesced back-to-back flushes stutter once the reply grows — so batch on a clock:
        // deltas pile up off-thread, and a timer paints at most ten times a second. 100ms batching is
        // invisible when reading streaming text; unbounded repainting is a frozen app.
        var pendingDelta = new StringBuilder();
        var pumping = false;   // guarded by deltaGate — the producers' view of "timer is running"
        var deltaGate = new object();

        var flushTimer = _dispatcher.CreateTimer();
        flushTimer.Interval = TimeSpan.FromMilliseconds(100);
        flushTimer.Tick += (_, _) =>
        {
            string chunk;
            lock (deltaGate)
            {
                chunk = pendingDelta.ToString();
                pendingDelta.Clear();
                if (chunk.Length == 0) pumping = false;
            }
            if (chunk.Length == 0) { flushTimer.Stop(); return; }   // idle — a new delta restarts it
            NoteAsk.AppendDelta(chunk);
        };

        void OnDelta(string delta)
        {
            lock (deltaGate)
            {
                pendingDelta.Append(delta);
                if (pumping) return;
                pumping = true;
            }
            OnUi(flushTimer.Start);
        }

        void FlushDelta()   // final drain on the UI thread, once the stream is over
        {
            string chunk;
            lock (deltaGate)
            {
                chunk = pendingDelta.ToString();
                pendingDelta.Clear();
                pumping = false;
            }
            flushTimer.Stop();
            if (chunk.Length > 0) NoteAsk.AppendDelta(chunk);
        }

        try
        {
            if (onNote)
            {
                await NoteAssistant.AskAboutNoteAsync(
                    endpoint, model!, note!.Title, NoteEditor.Body, selection,
                    thread, question, OnDelta, ct);
            }
            else
            {
                await NoteAssistant.AskAboutPadAsync(
                    endpoint, model!, _noteCache, NotesForFullRead(),
                    thread, question, OnDelta, ct);
            }

            FlushDelta();   // drain the tail before reading NoteAsk.Reply below
            NoteAsk.EndReply();

            thread.Add(new NoteAssistant.Turn(true, question));
            thread.Add(new NoteAssistant.Turn(false, NoteAsk.Reply));
            if (thread.Count > MaxThreadTurns) thread.RemoveRange(0, thread.Count - MaxThreadTurns);
        }
        catch (OperationCanceledException)
        {
            FlushDelta();   // show what had streamed before the stop
            NoteAsk.EndReply(label: "stopped");
        }
        catch (Exception ex)
        {
            FlushDelta();   // stop the timer — a late tick would paint reply text over the error
            // Ollama down, model pulled away, endpoint wrong: say which, in the strip.
            NoteAsk.EndReply(error: $"{ex.Message}\n\nEndpoint: {endpoint} · model: {model}");
        }
    }

    /// <summary>One-shot informational dialog. The panel's failures are all "the filesystem said no",
    /// which needs a sentence and an OK, not an InfoBar to dismiss.</summary>
    private async Task SayAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
