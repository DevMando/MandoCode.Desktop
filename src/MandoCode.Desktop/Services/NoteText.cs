namespace MandoCode.Desktop.Services;

/// <summary>
/// Newline handling between a note file and the TextBox that edits it. Extracted from
/// <c>NoteEditorPane</c> (which can't be compiled into the WinUI-free test project) because getting
/// this wrong is invisible in the app and destructive in the file.
///
/// A WinUI TextBox normalizes every newline to a bare CR on assignment, so <c>Editor.Text</c> is not
/// the string that was handed to it. Two consequences, both of which cost real notes before this
/// existed:
/// <list type="number">
///   <item>Writing <c>Editor.Text</c> straight back out converts a Notepad-authored CRLF note to
///   CR-only, which Notepad then renders as one enormous line.</item>
///   <item>Comparing the original file text against <c>Editor.Text</c> to detect edits reports a
///   difference the moment a note is merely OPENED, so an untouched note gets autosaved — bumping
///   its modified time and logging phantom edits to its shadow.</item>
/// </list>
/// </summary>
public static class NoteText
{
    /// <summary>The note's own line ending: CRLF if it has any, else LF if it has any, else the
    /// platform default for a file with no newline yet to copy.</summary>
    public static string DetectNewline(string fileText)
    {
        if (string.IsNullOrEmpty(fileText)) return Environment.NewLine;
        if (fileText.Contains("\r\n", StringComparison.Ordinal)) return "\r\n";
        return fileText.Contains('\n') ? "\n" : Environment.NewLine;
    }

    /// <summary>Editor text → file text: collapse whatever form the control holds newlines in, then
    /// write them back out as <paramref name="newline"/> — the convention the note already used.</summary>
    public static string ToFileText(string editorText, string newline) =>
        editorText.Replace("\r\n", "\n", StringComparison.Ordinal)
                  .Replace('\r', '\n')
                  .Replace("\n", newline, StringComparison.Ordinal);
}
