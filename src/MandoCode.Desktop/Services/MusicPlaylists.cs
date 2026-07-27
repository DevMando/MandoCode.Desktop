using System.Diagnostics;
using System.Reflection;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Junction-backed user playlists for the music player. A playlist is a directory junction
/// under <c>~\.mandocode\music</c> pointing at any local folder of MP3s: the harness's
/// folder-scan discovery walks straight through junctions, so neither the engine nor the CLI
/// (which shares the music root) needs a playlist concept of its own. Junctions rather than
/// symlinks because they need no admin rights; the tradeoff is local volumes only — no UNC
/// targets. WinUI-free so the pieces most likely to break on a pin roll live where a test
/// can reach them.
/// </summary>
public static class MusicPlaylists
{
    /// <summary>Case-insensitive name equality. Engine discovery lowercases user folder names
    /// but leaves embedded genres raw, so every playlist-name comparison must ignore case —
    /// one definition, used everywhere, instead of each call site remembering the rule.</summary>
    public static bool SameName(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is a reparse point (junction). Reads the
    /// link entry's own attributes — deliberately NOT <c>Directory.Exists</c>, which resolves
    /// the target and can block on a junction into an unplugged drive.</summary>
    public static bool IsJunction(string path)
    {
        try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>Finds an existing playlist junction already pointing at
    /// <paramref name="targetFolder"/> — compared by resolved path, not by name, so re-adding
    /// a folder reuses its playlist instead of minting a numbered twin. Null when none.</summary>
    public static string? FindExistingFor(string musicRoot, string targetFolder)
    {
        try
        {
            if (!Directory.Exists(musicRoot)) return null;
            var target = CanonicalPath(targetFolder);
            foreach (var dir in new DirectoryInfo(musicRoot).EnumerateDirectories())
            {
                var resolved = dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (resolved != null && string.Equals(CanonicalPath(resolved), target, StringComparison.OrdinalIgnoreCase))
                    return dir.Name;
            }
        }
        catch { /* unreadable entries just mean no match */ }
        return null;
    }

    /// <summary>Playlist name from the target folder's own name — scrubbed by the same rules
    /// as note titles, uniquified against the music root like snapshot titles ("beats",
    /// "beats (2)", …).</summary>
    public static string MakeUniqueName(string musicRoot, string targetFolder)
    {
        var baseName = NoteStore.SanitizeTitle(Path.GetFileName(Path.TrimEndingDirectorySeparator(targetFolder)));
        if (baseName.Length == 0) baseName = "playlist";

        var taken = Directory.Exists(musicRoot)
            ? Directory.EnumerateDirectories(musicRoot).Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();
        return SnapshotNaming.MakeUnique(baseName, taken);
    }

    /// <summary>Creates the junction via cmd's <c>mklink /J</c> — the only junction API that
    /// needs neither admin rights nor P/Invoke. Throws with mklink's own message on failure.</summary>
    public static async Task CreateAsync(string musicRoot, string name, string targetFolder)
    {
        Directory.CreateDirectory(musicRoot);
        var link = Path.Combine(musicRoot, name);

        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{targetFolder}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start cmd.exe.");
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            var err = (await proc.StandardError.ReadToEndAsync()).Trim();
            throw new InvalidOperationException(err.Length > 0 ? err : $"mklink exited with code {proc.ExitCode}.");
        }
    }

    /// <summary>Deletes only the junction — <c>recursive: false</c> on a reparse point removes
    /// the link itself and structurally cannot touch the target folder's contents.</summary>
    public static void Remove(string musicRoot, string name)
        => Directory.Delete(Path.Combine(musicRoot, name), recursive: false);

    /// <summary>The engine discovers tracks once, in its constructor, and exposes no re-scan.
    /// Until it grows a public rediscover API (backlogged for the next pin roll), invoke the
    /// private scan by reflection. False means "restart to see the change" — the honest
    /// fallback if a future harness renames the method.</summary>
    public static bool TryRediscover(MusicPlayerService music)
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

    private static string CanonicalPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
