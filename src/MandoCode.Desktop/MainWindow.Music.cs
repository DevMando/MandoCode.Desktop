using System.Diagnostics;
using System.Reflection;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    // ============================================================
    // Music flyout — UI over the harness's app-wide MusicPlayerService (one audio device).
    // The service has no change events and tracks LOOP rather than auto-advance, so state
    // only moves when a button here (or a /music command) moves it: refreshing on flyout
    // open and after each action is complete coverage, no polling.
    // ============================================================

    private MusicPlayerService Music => App.Services.GetRequiredService<MusicPlayerService>();

    /// <summary>Guards the genre combo's SelectionChanged while RefreshMusicUi repopulates it.</summary>
    private bool _loadingMusicUi;

    /// <summary>Keeps the poll timer alive: a DispatcherQueueTimer referenced only by a local
    /// is garbage-collected mid-flight and simply stops ticking — the icon then freezes in
    /// whatever state it was in until something else forces a refresh.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _musicPollTimer;

    /// <summary>The service has no change events, and playback can be driven from outside this
    /// flyout (/music chat commands). A 2-second property poll keeps the rail icon truthful; the
    /// tick is two boolean reads, so it just runs for the window's lifetime. Called once from
    /// the constructor.</summary>
    private void WireMusicPolling()
    {
        _musicPollTimer = _dispatcher.CreateTimer();
        _musicPollTimer.Interval = TimeSpan.FromSeconds(2);
        _musicPollTimer.Tick += (_, _) => UpdateMusicRailIcon(Music);
        _musicPollTimer.Start();
    }

    private void MusicFlyout_Opening(object sender, object e) => RefreshMusicUi();

    private void RefreshMusicUi()
    {
        var music = Music;
        _loadingMusicUi = true;
        try
        {
            var genres = music.GetAvailableGenres();
            MusicGenreCombo.ItemsSource = genres;
            MusicGenreCombo.SelectedItem = genres.FirstOrDefault(g =>
                string.Equals(g, music.Genre, StringComparison.OrdinalIgnoreCase)) ?? genres.FirstOrDefault();

            var hasTracks = genres.Count > 0;
            MusicPlayPauseButton.IsEnabled = hasTracks;
            MusicNextButton.IsEnabled = hasTracks && music.IsPlaying;
            MusicStopButton.IsEnabled = music.IsPlaying || music.IsPaused;
            MusicGenreCombo.IsEnabled = hasTracks;

            MusicVolumeSlider.Value = Math.Clamp(music.Volume * 100, 0, 100);

            MusicTrackText.Text = music.CurrentTrack is { } track
                ? (music.IsPaused ? $"Paused — {track.Name}" : $"{track.Name}  ·  {track.Genre}")
                : "Nothing playing";

            // Play glyph when stopped/paused, pause glyph while playing.
            MusicPlayPauseIcon.Glyph = music.IsPlaying && !music.IsPaused ? "" : "";

            var hint = music.AudioError
                       ?? (hasTracks ? null : $"No MP3s found. A playlist is just a folder of MP3s under {music.UserMusicPath} (e.g. \\lofi).");
            MusicHintText.Text = hint ?? "";
            MusicHintText.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;

            UpdateMusicRailIcon(music);
            UpdateRemovePlaylistButton();
        }
        finally
        {
            _loadingMusicUi = false;
        }
    }

    // ============================================================
    // User playlists — a playlist is a directory JUNCTION under ~\.mandocode\music pointing at
    // any folder of MP3s the user picked. The harness's folder-scan discovery walks straight
    // through junctions, so this needs no engine change and the CLI sees the same playlists.
    // Junctions (not symlinks) because they need no admin rights; the tradeoff is local
    // volumes only — no UNC targets.
    // ============================================================

    private async void MusicAddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        // Unpackaged apps must initialize pickers with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var music = Music;

        // Re-adding a folder that's already a playlist selects the existing one — matched by
        // where the junction actually points, not by name, so no "beats 2" twins.
        if (FindExistingPlaylistFor(music.UserMusicPath, folder.Path) is { } existing)
        {
            SelectPlaylist(existing);
            ShowMusicHint($"“{existing}” already points at that folder — selected it.");
            return;
        }

        string link;
        try
        {
            Directory.CreateDirectory(music.UserMusicPath);
            link = Path.Combine(music.UserMusicPath, MakeUniquePlaylistName(music.UserMusicPath, folder.Path));

            // cmd's mklink /J is the only junction API that needs neither admin rights nor P/Invoke.
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{folder.Path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Couldn't start cmd.exe.");
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException((await proc.StandardError.ReadToEndAsync()).Trim() is { Length: > 0 } err
                    ? err : $"mklink exited with code {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            ShowMusicHint($"Couldn't add that playlist: {ex.Message}");
            return;
        }

        var rediscovered = TryRediscoverTracks(music);
        RefreshMusicUi();
        SelectPlaylist(Path.GetFileName(link));

        var mp3Count = Directory.EnumerateFiles(folder.Path, "*.mp3").Count();
        ShowMusicHint(!rediscovered
            ? "Playlist added — restart MandoCode to see it."
            : mp3Count == 0
                ? "Playlist added, but no MP3s sit at the top level of that folder."
                : $"Added “{Path.GetFileName(link)}” ({mp3Count} track{(mp3Count == 1 ? "" : "s")}).");
    }

    private void MusicRemovePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (MusicGenreCombo.SelectedItem is not string name) return;
        var music = Music;
        var link = Path.Combine(music.UserMusicPath, name);
        if (!IsJunction(link)) return;   // only ever delete pointers, never a real folder of files

        try
        {
            if (string.Equals(music.Genre, name, StringComparison.OrdinalIgnoreCase)
                && (music.IsPlaying || music.IsPaused))
                music.Stop();
            // recursive:false on a reparse point removes the junction itself; the target
            // folder and its MP3s are untouched.
            Directory.Delete(link, recursive: false);
        }
        catch (Exception ex)
        {
            ShowMusicHint($"Couldn't remove the playlist: {ex.Message}");
            return;
        }

        var rediscovered = TryRediscoverTracks(music);
        RefreshMusicUi();
        ShowMusicHint(rediscovered ? $"Removed “{name}” — its folder is untouched."
                                   : "Playlist removed — restart MandoCode to update the list.");
    }

    /// <summary>Remove only offers itself for junction-backed playlists. Embedded genres have no
    /// folder, and a real folder of files is not ours to delete from a flyout.</summary>
    private void UpdateRemovePlaylistButton()
    {
        var visible = MusicGenreCombo.SelectedItem is string name
                      && IsJunction(Path.Combine(Music.UserMusicPath, name));
        MusicRemovePlaylistButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects a playlist in the combo by name — case-insensitively, since discovery
    /// may re-case folder names. Selection IS "loading": the change handler switches live
    /// playback to it, or arms it as what the play button will start.</summary>
    private void SelectPlaylist(string name)
    {
        var match = MusicGenreCombo.Items.OfType<string>()
            .FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));
        if (match != null) MusicGenreCombo.SelectedItem = match;
    }

    /// <summary>Finds a junction under the music root already pointing at <paramref name="targetFolder"/>,
    /// comparing resolved paths rather than playlist names. mklink stores targets in NT form
    /// (\??\C:\…), so prefixes are stripped before comparing.</summary>
    private static string? FindExistingPlaylistFor(string musicRoot, string targetFolder)
    {
        try
        {
            if (!Directory.Exists(musicRoot)) return null;
            var target = NormalizePath(targetFolder);
            foreach (var dir in Directory.EnumerateDirectories(musicRoot))
            {
                if (!IsJunction(dir)) continue;
                var linkTarget = new DirectoryInfo(dir).LinkTarget;
                if (linkTarget != null && string.Equals(NormalizePath(linkTarget), target, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            }
        }
        catch { /* unreadable entries just mean no match */ }
        return null;
    }

    private static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsJunction(string path)
    {
        try
        {
            return Directory.Exists(path)
                   && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    /// <summary>Playlist name from the target folder's own name, uniquified against whatever is
    /// already under the music root ("beats", "beats 2", …).</summary>
    private static string MakeUniquePlaylistName(string musicRoot, string targetFolder)
    {
        var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetFolder));
        foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        if (baseName.Length == 0) baseName = "playlist";

        var name = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(musicRoot, name)); n++)
            name = $"{baseName} {n}";
        return name;
    }

    /// <summary>The harness discovers tracks once, in its constructor, and exposes no re-scan.
    /// Until the engine grows a public rediscover API (backlogged for the next pin roll), invoke
    /// the private scan by reflection — with an honest "restart to see it" fallback if a future
    /// harness renames it.</summary>
    private static bool TryRediscoverTracks(MusicPlayerService music)
    {
        try
        {
            var discover = typeof(MusicPlayerService)
                .GetMethod("DiscoverTracks", BindingFlags.Instance | BindingFlags.NonPublic);
            if (discover == null) return false;
            discover.Invoke(music, null);
            return true;
        }
        catch { return false; }
    }

    private void ShowMusicHint(string text)
    {
        MusicHintText.Text = text;
        MusicHintText.Visibility = Visibility.Visible;
    }

    private bool _eqAnimating;
    private string? _musicTooltip;

    /// <summary>The rail icon carries the state worth showing while the flyout is closed: an
    /// animated gold equalizer while music plays (the glyph hides behind it), and a tooltip
    /// naming the track — hover answers "what's this song" without opening anything.
    /// Runs on a 2s poll, so both the storyboard and the tooltip only move on actual state
    /// change — Begin() on a running storyboard visibly restarts the bounce.</summary>
    private void UpdateMusicRailIcon(MusicPlayerService music)
    {
        var audible = music.IsPlaying && !music.IsPaused;
        if (audible != _eqAnimating)
        {
            _eqAnimating = audible;
            NavMusicIcon.Visibility = audible ? Visibility.Collapsed : Visibility.Visible;
            MusicEqPanel.Visibility = audible ? Visibility.Visible : Visibility.Collapsed;
            if (audible) MusicEqStoryboard.Begin();
            else MusicEqStoryboard.Stop();
        }

        var tooltip = music.CurrentTrack is { } track
            ? (audible ? $"Playing — {track.Name}" : $"Paused — {track.Name}")
            : "Music — background playlists while you work";
        if (tooltip != _musicTooltip)
        {
            _musicTooltip = tooltip;
            ToolTipService.SetToolTip(NavMusic, tooltip);
        }
    }

    private void MusicPlayPause_Click(object sender, RoutedEventArgs e)
    {
        var music = Music;
        if (music.IsPlaying || music.IsPaused) music.TogglePause();
        else music.Play(MusicGenreCombo.SelectedItem as string);
        RefreshMusicUi();
    }

    private void MusicNext_Click(object sender, RoutedEventArgs e)
    {
        Music.NextTrack();
        RefreshMusicUi();
    }

    private void MusicStop_Click(object sender, RoutedEventArgs e)
    {
        Music.Stop();
        RefreshMusicUi();
    }

    private void MusicGenre_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMusicUi || MusicGenreCombo.SelectedItem is not string genre) return;

        UpdateRemovePlaylistButton();

        // Record the pick as the service's current genre even while idle. The flyout closes
        // (and this combo unloads) around Add-playlist's folder picker, and the next
        // RefreshMusicUi re-selects from music.Genre — without this line the dropdown snaps
        // back to the previous playlist on reopen.
        App.Services.GetRequiredService<MandoCodeConfig>().Music.Genre = genre;

        // Switching playlist while playing jumps to it immediately; while idle it just becomes
        // what the play button will start.
        var music = Music;
        if (music.IsPlaying || music.IsPaused)
        {
            music.Play(genre);
            RefreshMusicUi();
        }
    }

    private void MusicVolume_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loadingMusicUi) return;
        Music.SetVolume((float)(e.NewValue / 100.0));
    }
}
