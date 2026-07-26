namespace MandoCode.Desktop.Services;

/// <summary>
/// One note found on disk. Notes are app-wide — a jot pad, not a project artifact — so unlike a
/// snapshot or an archived conversation this is only a point-in-time reading of a file under
/// <see cref="NoteStore.Root"/>. Nothing here is authoritative: the file is. That's what makes a note
/// written in Notepad show up and a note deleted outside the app disappear, with no index to drift.
///
/// Pure data + display derivations, no UI types: discovery runs off the UI thread.
/// </summary>
public sealed record NoteEntry
{
    /// <summary>Absolute path to the note file.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Optional folder this note is filed under, relative to <see cref="NoteStore.Root"/> — empty for
    /// a note sitting loose at the top. It's a plain subfolder name rather than metadata precisely so
    /// there's nothing to keep in sync: the filesystem holds the grouping, and re-filing a note by
    /// dragging it between folders in Explorer just works. New notes are stamped with the active
    /// agent's project folder when there is one, which is what lets the panel group by project
    /// without notes being owned by a project.
    /// </summary>
    public required string Group { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }
    public required long Bytes { get; init; }

    /// <summary>First non-empty line, trimmed for the card. Empty for an untouched note.</summary>
    public required string Preview { get; init; }

    /// <summary>The note's text as read at discovery, capped by <see cref="NoteStore.MaxTextBytes"/> —
    /// what search matches against, and what the assistant is given. Not the editor's copy: opening a
    /// note always re-reads the file.</summary>
    public required string Text { get; init; }

    /// <summary>File name with extension ("ideas.txt") — a note's identity in its folder.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Card title: the file name without its extension.</summary>
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Group heading for the panel. Notes with no folder collect under one heading rather
    /// than floating above the groups, so the list has exactly one shape.</summary>
    public string GroupLabel => string.IsNullOrEmpty(Group) ? "Unfiled" : Group;

    public string TimeLabel => ProjectDisplay.TimeLabel(ModifiedAt);

    /// <summary>"empty" / "412 B" / "3.1 KB" — a note's length is most of what tells you whether it's
    /// a stub or something you actually wrote.</summary>
    public string SizeLabel => Bytes switch
    {
        <= 0 => "empty",
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:0.#} KB",
        _ => $"{Bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
