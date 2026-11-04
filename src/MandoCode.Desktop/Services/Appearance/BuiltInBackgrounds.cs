namespace MandoCode.Desktop.Services;

/// <summary>One chat background that ships with the app, as the Appearance gallery shows it.</summary>
/// <param name="FileName">Name on disk — the durable identity persisted in ui-settings.json.</param>
/// <param name="DisplayName">Label derived from the file name (see
/// <see cref="BuiltInBackgrounds.DisplayNameFor"/>).</param>
/// <param name="FullPath">Absolute path in the install folder, used for the thumbnail and as the
/// copy source when the user picks it.</param>
public sealed record BuiltInBackground(string FileName, string DisplayName, string FullPath);

/// <summary>
/// The background images bundled with a release, offered alongside "Choose image…" so a fresh
/// install has something to pick without hunting for a file.
///
/// Deliberately DISCOVERED rather than hard-coded: the gallery is whatever ships in
/// <c>Assets/images/backgrounds</c>, so adding a fourth image in a future release is a file drop
/// plus a line in the changelog — no code change, no list to keep in sync. The folder's README
/// documents the naming convention for whoever adds the next one.
///
/// Selecting one goes through the SAME path as a user-picked file
/// (<see cref="ThemeManager.SetChatBackground"/> copies it into the user-data folder, which the
/// per-tab WebView serves over the <c>mandocode.userdata</c> host). So a built-in needs no special
/// case anywhere downstream — not in the CSS variable, the live preview, or the export.
/// </summary>
public static class BuiltInBackgrounds
{
    /// <summary>Extensions offered in the gallery — the subset of the file picker's filters that
    /// WinUI's <c>BitmapImage</c> can decode for a thumbnail.</summary>
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Where the shipped images live next to the executable. Copied there by the csproj's
    /// Content include, so this resolves in a dev build and an installed one alike.</summary>
    public static string Folder =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "images", "backgrounds");

    /// <summary>
    /// The shipped gallery, ordered by file name — a numeric prefix ("01-", "02-") is how a release
    /// controls the order without that prefix showing in the UI. Read once: the install folder can't
    /// change while the app is running.
    ///
    /// Empty is a valid, supported state, not an error: a dev tree with no images yet simply hides
    /// the gallery and leaves "Choose image…" working exactly as before.
    /// </summary>
    public static IReadOnlyList<BuiltInBackground> All { get; } = Discover();

    /// <summary>The shipped image with this file name, or null when it isn't in this release —
    /// which is what happens to a saved choice if a later version drops an image.</summary>
    public static BuiltInBackground? Find(string? fileName) =>
        string.IsNullOrEmpty(fileName)
            ? null
            : All.FirstOrDefault(b => string.Equals(b.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<BuiltInBackground> Discover() => DiscoverIn(Folder);

    /// <summary>The gallery for an arbitrary folder — <see cref="Discover"/>'s body, split out so the
    /// ordering and extension rules the folder's README promises can be tested against a temp
    /// directory instead of the install folder.</summary>
    public static IReadOnlyList<BuiltInBackground> DiscoverIn(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<BuiltInBackground>();

            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new BuiltInBackground(Path.GetFileName(f), DisplayNameFor(f), f))
                .ToList();
        }
        catch
        {
            // An unreadable install folder must not take the Appearance page down — the gallery
            // just doesn't appear, and picking your own file still works.
            return Array.Empty<BuiltInBackground>();
        }
    }

    /// <summary>
    /// A label from a file name: <c>01-nebula-drift.jpg</c> → "Nebula Drift". The leading
    /// <c>NN-</c> is an ordering device for the release, so it's stripped rather than shown, and
    /// dashes/underscores become spaces. Falls back to the bare file name if that leaves nothing.
    /// </summary>
    public static string DisplayNameFor(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);

        var dash = stem.IndexOfAny(new[] { '-', '_' });
        if (dash > 0 && stem[..dash].All(char.IsDigit)) stem = stem[(dash + 1)..];

        var words = stem.Split('-', '_', ' ')
            .Where(w => w.Length > 0)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);

        var name = string.Join(' ', words);
        return name.Length == 0 ? Path.GetFileNameWithoutExtension(path) : name;
    }
}
