namespace MandoCode.Desktop.Services;

/// <summary>
/// Shared display formatting for the Snapshots and History panels. The project-folder leaf name
/// and the "MMM d · h:mm tt" timestamp were duplicated verbatim on <see cref="ContextSnapshot"/>
/// and <see cref="SessionArchiveEntry"/>; one home keeps the two panels visually identical.
/// </summary>
public static class ProjectDisplay
{
    /// <summary>The project folder's leaf name, or a stand-in when the root is unknown/blank
    /// (an older file that predates project tracking, or a conversation held outside any folder).</summary>
    public static string ProjectLabel(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return "Unknown project";
        var name = System.IO.Path.GetFileName(
            projectRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? projectRoot! : name;
    }

    /// <summary>Local "MMM d · h:mm tt" label for a captured/closed timestamp.</summary>
    public static string TimeLabel(DateTimeOffset when) => when.LocalDateTime.ToString("MMM d · h:mm tt");
}
