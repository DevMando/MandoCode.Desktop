using MandoCode.Services;
using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    // ============================================================
    // Music flyout — UI over the harness's app-wide MusicPlayerService (one audio device).
    // The service changes state on its own — auto-advance swaps CurrentTrack when a song
    // ends, and a device failure stops playback with only AudioError to show for it — but
    // exposes no events, so a 2-second poll watches for movement (WireMusicPolling).
    // Playlist add/remove (directory junctions) lives in Services/MusicPlaylists.
    // ============================================================

    private readonly MusicPlayerService _music;

    /// <summary>Guards the playlist combo's SelectionChanged while RefreshMusicUi repopulates it.</summary>
    private bool _loadingMusicUi;

    private void MusicFlyout_Opening(object sender, object e) => RefreshMusicUi();

    /// <summary>Full rebuild: playlist list, selection, volume, empty state. Only for flyout
    /// open and playlist add/remove — repopulating ItemsSource on every transport click would
    /// churn selection (and close the dropdown if it's expanded under the user).</summary>
    private void RefreshMusicUi()
    {
        _loadingMusicUi = true;
        try
        {
            var genres = _music.GetAvailableGenres();
            MusicGenreCombo.ItemsSource = genres;
            MusicGenreCombo.SelectedItem = genres.FirstOrDefault(g => MusicPlaylists.SameName(g, _music.Genre))
                                           ?? genres.FirstOrDefault();

            var hasTracks = genres.Count > 0;
            MusicGenreCombo.IsEnabled = hasTracks;
            MusicPlayPauseButton.IsEnabled = hasTracks;
            MusicVolumeSlider.Value = _music.Volume * 100;

            MusicHintText.Visibility = Visibility.Collapsed;
            if (!hasTracks)
                ShowMusicHint($"No MP3s found. A playlist is just a folder of MP3s under {_music.UserMusicPath} (e.g. \\lofi).");

            UpdateRemovePlaylistButton();
        }
        finally
        {
            _loadingMusicUi = false;
        }
        RefreshTransportState();
    }

    /// <summary>The parts that move during playback: track line, button states, play/pause
    /// glyph, rail icon — and any AudioError, surfaced the moment the poll sees it.</summary>
    private void RefreshTransportState()
    {
        MusicNextButton.IsEnabled = _music.IsPlaying;
        MusicStopButton.IsEnabled = _music.IsPlaying || _music.IsPaused;

        // IsPlaying and IsPaused are mutually exclusive in the service — paused means
        // IsPlaying == false — so IsPlaying alone answers "is audio actually flowing".
        MusicTrackText.Text = _music.CurrentTrack is { } track
            ? (_music.IsPaused ? $"Paused — {track.Name}" : $"{track.Name}  ·  {track.Genre}")
            : "Nothing playing";
        MusicPlayPauseIcon.Glyph = _music.IsPlaying ? "" : "";   // pause : play

        if (_music.AudioError is { } error) ShowMusicHint(error);

        UpdateMusicRailIcon();
    }

    // ============================================================
    // Rail icon + poll
    // ============================================================

    private string? _musicTooltip;

    /// <summary>The rail icon carries the state worth showing while the flyout is closed: an
    /// animated gold equalizer while music plays (the glyph hides behind it), and a tooltip
    /// naming the track — hover answers "what's this song" without opening anything. Runs on
    /// the poll, so both only move on actual change: Begin() on a running storyboard visibly
    /// restarts the bounce, and rewriting an open tooltip dismisses it.</summary>
    private void UpdateMusicRailIcon()
    {
        var audible = _music.IsPlaying;
        if (audible != (MusicEqPanel.Visibility == Visibility.Visible))
        {
            NavMusicIcon.Visibility = audible ? Visibility.Collapsed : Visibility.Visible;
            MusicEqPanel.Visibility = audible ? Visibility.Visible : Visibility.Collapsed;
            if (audible) MusicEqStoryboard.Begin();
            else MusicEqStoryboard.Stop();
        }

        var tooltip = _music.CurrentTrack is { } track
            ? (audible ? $"Playing — {track.Name}" : $"Paused — {track.Name}")
            : "Music — background playlists while you work";
        if (tooltip != _musicTooltip)
        {
            _musicTooltip = tooltip;
            ToolTipService.SetToolTip(NavMusic, tooltip);
        }
    }

    /// <summary>Kept in a field: a DispatcherQueueTimer referenced only by a local is
    /// garbage-collected mid-flight and simply stops ticking. Stopped in MainWindow_Closed.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _musicPollTimer;
    private string? _musicStateKey;

    /// <summary>Watches for state the service changes on its own (auto-advance, device
    /// failure) or that /music chat commands change from outside this flyout. Each tick
    /// compares a small state key and touches the UI only when it moved — the open flyout
    /// gets a transport refresh (so the track line follows an auto-advance), the closed one
    /// just the rail icon. Called once from the constructor.</summary>
    private void WireMusicPolling()
    {
        _musicPollTimer = _dispatcher.CreateTimer();
        _musicPollTimer.Interval = TimeSpan.FromSeconds(2);
        _musicPollTimer.Tick += (_, _) =>
        {
            var key = $"{_music.IsPlaying}|{_music.IsPaused}|{_music.CurrentTrack?.Name}|{_music.AudioError}";
            if (key == _musicStateKey) return;
            _musicStateKey = key;

            if (MusicFlyout.IsOpen) RefreshTransportState();
            else UpdateMusicRailIcon();
        };
        _musicPollTimer.Start();

        UpdateMusicRailIcon();   // seed the icon and tooltip (the tooltip is only set here, not in XAML)
    }

    // ============================================================
    // Transport + playlist selection
    // ============================================================

    private void MusicPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_music.IsPlaying || _music.IsPaused) _music.TogglePause();
        else _music.Play(MusicGenreCombo.SelectedItem as string);
        RefreshTransportState();
    }

    private void MusicNext_Click(object sender, RoutedEventArgs e)
    {
        _music.NextTrack();
        RefreshTransportState();
    }

    private void MusicStop_Click(object sender, RoutedEventArgs e)
    {
        _music.Stop();
        RefreshTransportState();
    }

    private void MusicGenre_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMusicUi || MusicGenreCombo.SelectedItem is not string genre) return;

        UpdateRemovePlaylistButton();

        // Record the pick through the config owner even while idle. The flyout closes (and
        // this combo unloads) around Add-playlist's folder picker, and RefreshMusicUi
        // re-selects from music.Genre — without this the dropdown snaps back to the previous
        // playlist on reopen. Saving also makes the pick survive a restart, like the volume
        // already does via the service's own SavePreferences.
        _configs.Defaults.Music.Genre = genre;
        _configs.SaveDefaults();

        // Switching playlist while playing jumps to it immediately; while idle it just
        // becomes what the play button will start.
        if (_music.IsPlaying || _music.IsPaused)
        {
            _music.Play(genre);
            RefreshTransportState();
        }
    }

    private void MusicVolume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingMusicUi) return;
        _music.SetVolume((float)(e.NewValue / 100.0));
    }

    // ============================================================
    // Playlist add / remove — thin UI over Services/MusicPlaylists
    // ============================================================

    private async void MusicAddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        // Unpackaged apps must initialize pickers with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        // Re-adding a folder that's already a playlist selects the existing one.
        if (MusicPlaylists.FindExistingFor(_music.UserMusicPath, folder.Path) is { } existing)
        {
            SelectPlaylist(existing);
            ShowMusicHint($"“{existing}” already points at that folder — selected it.");
            return;
        }

        var name = MusicPlaylists.MakeUniqueName(_music.UserMusicPath, folder.Path);
        try
        {
            await MusicPlaylists.CreateAsync(_music.UserMusicPath, name, folder.Path);
        }
        catch (Exception ex)
        {
            ShowMusicHint($"Couldn't add that playlist: {ex.Message}");
            return;
        }

        // Off the UI thread: the rescan walks every playlist folder, including junctions
        // into arbitrarily large directories.
        var rediscovered = await Task.Run(() => MusicPlaylists.TryRediscover(_music));
        RefreshMusicUi();
        SelectPlaylist(name);

        string message;
        if (!rediscovered)
        {
            message = "Playlist added — restart MandoCode to see it.";
        }
        else
        {
            var tracks = _music.GetAvailableTracks(name).Count;
            message = tracks == 0
                ? "Playlist added, but no MP3s sit at the top level of that folder."
                : $"Added “{name}” ({tracks} track{(tracks == 1 ? "" : "s")}).";
        }
        ShowMusicHint(message);
    }

    private async void MusicRemovePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (MusicGenreCombo.SelectedItem is not string name) return;
        // Pointers only, never a real folder of files — same gate as the button's visibility.
        if (!MusicPlaylists.IsJunction(Path.Combine(_music.UserMusicPath, name))) return;

        try
        {
            if (MusicPlaylists.SameName(_music.Genre, name) && (_music.IsPlaying || _music.IsPaused))
                _music.Stop();
            MusicPlaylists.Remove(_music.UserMusicPath, name);
        }
        catch (Exception ex)
        {
            ShowMusicHint($"Couldn't remove the playlist: {ex.Message}");
            return;
        }

        var rediscovered = await Task.Run(() => MusicPlaylists.TryRediscover(_music));
        RefreshMusicUi();
        ShowMusicHint(rediscovered ? $"Removed “{name}” — its folder is untouched."
                                   : "Playlist removed — restart MandoCode to update the list.");
    }

    /// <summary>Remove only offers itself for junction-backed playlists. Embedded genres have
    /// no folder, and a real folder of files is not ours to delete from a flyout.</summary>
    private void UpdateRemovePlaylistButton()
    {
        var visible = MusicGenreCombo.SelectedItem is string name
                      && MusicPlaylists.IsJunction(Path.Combine(_music.UserMusicPath, name));
        MusicRemovePlaylistButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects a playlist in the combo by name. Selection IS "loading": the change
    /// handler records it as the service's genre and switches live playback to it.</summary>
    private void SelectPlaylist(string name)
    {
        var match = MusicGenreCombo.Items.OfType<string>().FirstOrDefault(g => MusicPlaylists.SameName(g, name));
        if (match != null) MusicGenreCombo.SelectedItem = match;
    }

    private void ShowMusicHint(string text)
    {
        MusicHintText.Text = text;
        MusicHintText.Visibility = Visibility.Visible;
    }
}
