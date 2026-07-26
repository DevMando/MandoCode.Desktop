namespace MandoCode.Desktop.Services;

/// <summary>
/// The jot pad: plain text files under one app-wide folder, <c>~/.mandocode/notes</c>.
///
/// Notes are global on purpose — the same call as snapshots and session history. A note is something
/// you want to write down *now*, which is often between projects or before an agent is even open, so
/// tying a note's existence to a project folder made the feature need permission to be used. What
/// survives of "which project was this about" is a plain SUBFOLDER: a new note is filed under the
/// active agent's folder name when there is one, and sits loose at the top when there isn't. Grouping
/// therefore costs no metadata, can't drift, and is fixed by dragging files around in Explorer.
///
/// <b>The filesystem is the store.</b> No index, no JSON. That's why the folder lives in
/// <c>~/.mandocode</c> (beside the CLI's own <c>config.json</c>) rather than in LocalAppData: these
/// are your files, meant to be greppable, syncable, and openable in any editor. Discovery is a walk
/// of one folder plus its immediate subfolders, which is nothing, and in exchange no row can ever
/// point at a file that isn't there.
///
/// Content is written in exactly one place — <c>NoteEditorPane</c>'s autosave, i.e. your own
/// keystrokes. The note assistant has no filesystem tools at all (see <see cref="NoteAssistant"/>),
/// so nothing it produces can reach a note except through an explicit Insert or Replace.
///
/// The root is injected rather than hard-coded so the tests drive the real thing against temp folders.
/// </summary>
public sealed class NoteStore
{
    /// <summary>Extensions treated as notes. <c>.txt</c> is what the app creates; <c>.md</c> is here
    /// because half the world's existing notes are markdown and refusing to list them would make the
    /// panel lie about the folder.</summary>
    public static readonly string[] NoteExtensions = { ".txt", ".md" };

    /// <summary>Cap on how much of a note is read into memory at discovery — the panel holds every
    /// note's text so search matches bodies without re-reading files per keystroke, and so the
    /// assistant can be handed a note without a second read. A longer note still lists and still opens
    /// in full; only search and the corpus prompt see a truncated body.</summary>
    public const int MaxTextBytes = 128 * 1024;

    private const int PreviewChars = 160;
    private const string DefaultTitle = "Untitled";

    /// <summary>Where notes live: <c>~/.mandocode/notes</c>, beside the config file the CLI shares.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "notes");

    /// <summary>The one folder this store owns.</summary>
    public string Root { get; }

    public NoteStore(string? root = null) => Root = root ?? DefaultRoot;

    /// <summary>Raised after a create/rename/delete so the panel can repopulate. Fires on the calling
    /// thread.</summary>
    public event Action? Changed;

    // ---- discovery ----

    /// <summary>
    /// Every note in the pad, newest-modified first: files at the top of <see cref="Root"/> (ungrouped)
    /// plus files one level down (grouped by folder name). Blocking file IO — callers run it off the UI
    /// thread. One level only: a jot pad with a folder hierarchy is a filing system, and the search box
    /// is a better answer to "where did I put it" than nesting.
    /// </summary>
    public IReadOnlyList<NoteEntry> Discover()
    {
        var notes = new List<NoteEntry>();

        try
        {
            if (!Directory.Exists(Root)) return notes;

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly))
                if (IsNoteFile(file)) notes.Add(Read(file, group: ""));

            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                // Dot-folders are somebody else's business (a .git the user put here, editor state).
                if (name.StartsWith('.')) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    if (IsNoteFile(file)) notes.Add(Read(file, group: name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable pad folder means an empty list, never a broken panel.
        }

        return notes.OrderByDescending(n => n.ModifiedAt).ToList();
    }

    private static NoteEntry Read(string path, string group)
    {
        try
        {
            var info = new FileInfo(path);
            var text = ReadCapped(path);
            return new NoteEntry
            {
                Path = info.FullName,
                Group = group,
                ModifiedAt = info.LastWriteTime,
                Bytes = info.Length,
                Preview = Preview(text),
                Text = text,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note held open by another program still deserves a row — list it without a body.
            return new NoteEntry
            {
                Path = path,
                Group = group,
                ModifiedAt = DateTimeOffset.MinValue,
                Bytes = 0,
                Preview = "(unreadable right now — open in another program?)",
                Text = "",
            };
        }
    }

    public static bool IsNoteFile(string path) =>
        NoteExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string ReadCapped(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxTextBytes];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    // ---- lifecycle ----

    /// <summary>
    /// Creates an empty note, filed under <paramref name="group"/> when given (the active agent's
    /// folder name), creating the pad and the subfolder on first use. A blank or colliding title is
    /// resolved rather than rejected — this is reached by clicking New mid-thought, so it must never
    /// stop to argue about a name.
    /// </summary>
    public NoteEntry Create(string? title = null, string? group = null)
    {
        var groupName = SanitizeTitle(group);
        var dir = groupName.Length == 0 ? Root : Path.Combine(Root, groupName);
        Directory.CreateDirectory(dir);

        var path = UniquePath(dir, SanitizeTitle(title) is { Length: > 0 } t ? t : DefaultTitle, ".txt");
        File.WriteAllText(path, "");

        var info = new FileInfo(path);
        var entry = new NoteEntry
        {
            Path = info.FullName,
            Group = groupName,
            ModifiedAt = info.LastWriteTime,
            Bytes = 0,
            Preview = "",
            Text = "",
        };
        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// Renames a note in place, keeping its extension and its folder. Returns the moved note, or null
    /// when the rename was a no-op or impossible (locked file, gone from disk) — the caller keeps
    /// showing the old entry rather than losing the note.
    /// </summary>
    public NoteEntry? Rename(NoteEntry note, string newTitle)
    {
        var clean = SanitizeTitle(newTitle);
        if (clean.Length == 0 || clean == note.Title) return null;

        var dir = Path.GetDirectoryName(note.Path)!;
        var target = UniquePath(dir, clean, Path.GetExtension(note.Path));

        try
        {
            File.Move(note.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var info = new FileInfo(target);
        var moved = note with { Path = info.FullName, ModifiedAt = info.LastWriteTime };
        Changed?.Invoke();
        return moved;
    }

    /// <summary>Deletes a note. Returns false if the file couldn't be removed, so the caller can say so
    /// instead of dropping a row that's still on disk.</summary>
    public bool Delete(NoteEntry note)
    {
        try
        {
            if (File.Exists(note.Path)) File.Delete(note.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Re-reads one note from disk, or null if it's gone. Used after an external change so a
    /// single card refreshes without rescanning the pad.</summary>
    public NoteEntry? Reread(NoteEntry note)
    {
        try
        {
            if (!File.Exists(note.Path)) return null;
            var info = new FileInfo(note.Path);
            var text = ReadCapped(note.Path);
            return note with
            {
                ModifiedAt = info.LastWriteTime,
                Bytes = info.Length,
                Preview = Preview(text),
                Text = text,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return note;
        }
    }

    // ---- pure helpers (directly unit tested) ----

    /// <summary>
    /// Strips what a file name can't hold and collapses whitespace, so a title typed in the rename box
    /// ("Q3 ideas: rollout/plan") becomes a legal file name instead of an exception. Leading dots go
    /// too — a note called ".secret" would be invisible in its own folder, and a dot-folder is skipped
    /// by discovery.
    /// </summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        var cleaned = new string(title
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)
            .ToArray());

        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Trim().TrimStart('.').Trim();
    }

    /// <summary>First free "&lt;base&gt;&lt;ext&gt;", "&lt;base&gt; 2&lt;ext&gt;", … in a folder.
    /// Case-insensitive, because Windows won't let "ideas.txt" and "Ideas.txt" coexist and silently
    /// overwriting one with the other is the worst outcome a notes app can have.</summary>
    public static string UniquePath(string dir, string baseName, string ext)
    {
        var candidate = Path.Combine(dir, baseName + ext);
        var n = 1;
        while (File.Exists(candidate))
        {
            n++;
            candidate = Path.Combine(dir, $"{baseName} {n}{ext}");
        }
        return candidate;
    }

    /// <summary>Card subtitle: the first line with anything on it, whitespace-collapsed and capped.
    /// The first line is where the gist goes, so it does a title's job on notes still called
    /// "Untitled".</summary>
    public static string Preview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ').Trim();
            if (line.Length == 0) continue;
            return line.Length <= PreviewChars ? line : line[..PreviewChars].TrimEnd() + "…";
        }
        return "";
    }

    /// <summary>Panel search: file name, group, and note BODY. Matching the body is the whole point —
    /// you remember what you wrote, not what you named it.</summary>
    public static bool Matches(NoteEntry note, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return note.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || note.GroupLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || note.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The matching line from a note's body, so a hit on text the card doesn't show explains
    /// itself. Null when the match came from the name or group, or when it's already the preview.</summary>
    public static string? MatchSnippet(NoteEntry note, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || note.Text.Length == 0) return null;

        foreach (var raw in note.Text.Split('\n'))
        {
            if (raw.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line == note.Preview) return null;   // already visible on the card
                return line.Length <= PreviewChars ? line : line[..PreviewChars].TrimEnd() + "…";
            }
        }
        return null;
    }
}
