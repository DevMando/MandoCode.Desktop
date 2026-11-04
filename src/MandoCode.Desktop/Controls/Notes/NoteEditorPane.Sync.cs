using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// Keeping the open note in step with the file underneath it. These are plain files in
/// ~/.mandocode/notes, so this editor is NOT the only thing that writes them: Notepad, VS Code, a sync
/// client, or a git checkout can all change one while it's open.
///
/// Everything here hangs off one decision — compare CONTENT, not timestamps. <c>_lastSavedText</c> is
/// what this editor believes is on disk, so an incoming change that matches it was ours, one that
/// differs while the buffer is clean can be adopted silently, and one that differs while there are
/// unsaved keystrokes is a genuine conflict only the user can resolve. No path here discards typing.
/// </summary>
public sealed partial class NoteEditorPane
{
    private enum Conflict { None, ChangedOnDisk, DeletedOnDisk }

    private FileSystemWatcher? _watcher;
    private Conflict _conflict;
    private string _conflictDiskText = "";

    // ---- external changes (Notepad, VS Code, sync, git) ----

    private void StartWatching(string path)
    {
        StopWatching();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No watcher (deleted folder, network path, exhausted handles) just means external edits
            // aren't noticed live. Editing still works, so this isn't worth a message.
            _watcher = null;
        }
    }

    private void StopWatching()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Deleted -= OnFileSystemEvent;
            _watcher.Renamed -= OnFileSystemEvent;
            _watcher.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _watcher = null;
    }

    /// <summary>Watcher callbacks arrive on a threadpool thread; everything below touches UI.</summary>
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        => _dispatcher.TryEnqueue(HandleExternalChange);

    private void HandleExternalChange()
    {
        if (Current == null) return;

        if (!File.Exists(Current.Path))
        {
            // Only interesting if we'd lose something. With a clean buffer the note is simply gone, and
            // the panel's own refresh will drop the row.
            if (_dirty)
                ShowConflict(Conflict.DeletedOnDisk,
                    "This note was deleted while you were typing.",
                    "Save it back", "Discard my text");
            else
                Deleted?.Invoke(Current);
            return;
        }

        string disk;
        try
        {
            disk = File.ReadAllText(Current.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // mid-write by someone else; the next event brings the settled content
        }

        // Compared in FILE form, since that's what the disk holds.
        if (disk == ToFileText(_lastSavedText)) return;     // that write was ours
        if (disk == ToFileText(Editor.Text))                // converged on the same content
        {
            _lastSavedText = Editor.Text;
            SetDirty(false);
            return;
        }

        if (!_dirty)
        {
            // Nothing of ours to lose: adopt it.
            var caret = Editor.SelectionStart;
            LoadIntoEditor(disk);
            Editor.Select(Math.Min(caret, Editor.Text.Length), 0);
            SetStatus($"Updated from disk {DateTime.Now:h:mm tt}");
            NotifyStored();
            return;
        }

        _conflictDiskText = disk;
        ShowConflict(Conflict.ChangedOnDisk,
            "This note changed on disk while you had unsaved edits.",
            "Use the version on disk", "Keep what I typed");
    }

    private void ShowConflict(Conflict kind, string message, string primary, string secondary)
    {
        _conflict = kind;
        _autosave.Stop();   // no autosave until the user chooses — either write would lose text
        ConflictBar.Message = message;
        ConflictPrimary.Content = primary;
        ConflictSecondary.Content = secondary;
        ConflictBar.IsOpen = true;
    }

    private void ClearConflict()
    {
        _conflict = Conflict.None;
        _conflictDiskText = "";
        ConflictBar.IsOpen = false;
    }

    private void ConflictPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;

        switch (_conflict)
        {
            case Conflict.ChangedOnDisk:
                LoadIntoEditor(_conflictDiskText);
                SetDirty(false);
                SetStatus("Loaded the version from disk");
                ClearConflict();
                NotifyStored();
                break;

            case Conflict.DeletedOnDisk:
                ClearConflict();
                SaveNow(force: true);     // put the note back, deliberately
                StartWatching(Current.Path);
                break;
        }
    }

    private void ConflictSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;

        switch (_conflict)
        {
            case Conflict.ChangedOnDisk:
                ClearConflict();
                SaveNow(force: true);     // keep mine: overwrite disk, deliberately
                break;

            case Conflict.DeletedOnDisk:
                var gone = Current;
                ClearConflict();
                SetDirty(false);
                Deleted?.Invoke(gone);
                break;
        }
    }
}
