using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MandoCode.Desktop.Controls;

/// <summary>
/// The note's own actions: rename in place, reveal, delete. Split out from the editor proper because
/// none of it touches the buffer — they act on the FILE, through the store.
/// </summary>
public sealed partial class NoteEditorPane
{
    private bool _renaming;

    // ---- rename ----

    private void Rename_Click(object sender, RoutedEventArgs e) => BeginRename();

    private void BeginRename()
    {
        if (Current == null) return;
        _renaming = true;
        RenameBox.Text = Current.Title;
        RenameBox.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Collapsed;
        RenameBox.Focus(FocusState.Programmatic);
        RenameBox.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (!_renaming) return;
        _renaming = false;

        RenameBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;

        if (!commit || Current == null || Store == null) return;

        // Save first: the rename moves the file, and a pending autosave would then write to a path that
        // no longer exists.
        FlushPendingSave();

        var moved = Store.Rename(Current, RenameBox.Text);
        if (moved == null)
        {
            SetStatus("Rename didn't stick — that name may be in use or the file is locked");
            return;
        }

        Current = moved;
        StartWatching(moved.Path);   // the old watcher was filtered to the old file name
        RefreshHeader();
        SetStatus($"Renamed {DateTime.Now:h:mm tt}");
        Renamed?.Invoke(moved);
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { EndRename(commit: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { EndRename(commit: false); e.Handled = true; }
    }

    /// <summary>Clicking away commits rather than discards — a typed name the user walked away from was
    /// still their intent.</summary>
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => EndRename(commit: true);

    // ---- menu actions ----

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null) return;
        var dir = Path.GetDirectoryName(Current.Path);
        if (dir == null) return;
        var failed = ShellOpen.Try(dir);
        if (failed != null) SetStatus($"Couldn't open the folder — {failed.Message}");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current == null || Store == null) return;

        var note = Current;
        var dialog = new ContentDialog
        {
            Title = "Delete note",
            Content = $"Delete “{note.FileName}”? The file is removed from disk. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Stop the debounce and the watcher before removing the file, so neither reacts to our own
        // delete (an autosave here would put the note straight back).
        _autosave.Stop();
        SetDirty(false);
        StopWatching();

        if (!Store.Delete(note))
        {
            SetStatus("Couldn't delete this note — it may be open in another program");
            StartWatching(note.Path);
            return;
        }

        Current = null;
        Deleted?.Invoke(note);
    }
}
