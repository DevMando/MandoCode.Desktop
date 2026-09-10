namespace MandoCode.Desktop.Services;

/// <summary>
/// Renders an agent shell command's lifecycle as terminal text — a prompt-style header, the
/// command's own lines, and a closing status. Kept separate from <see cref="AgentCommandLog"/>
/// and free of any UI type so the exact bytes that reach xterm can be asserted in tests.
///
/// <para>Colors are set with SGR escapes rather than the app's theme brushes because this text is
/// written into an xterm buffer, which knows nothing about XAML resources. The 256-color codes
/// chosen here match the terminal theme in <c>terminal.js</c>.</para>
/// </summary>
public static class AgentCommandFormat
{
    private const string Dim = "\x1b[38;5;244m";
    private const string Gold = "\x1b[38;5;214m";
    private const string Red = "\x1b[38;5;203m";
    private const string Green = "\x1b[38;5;114m";
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// The line announcing a command, styled like a shell prompt so the log reads the way a
    /// terminal session does. The working directory is shown because an agent's commands run in
    /// its own project root, which is not necessarily the folder the user is looking at.
    /// </summary>
    public static string Header(string command, string workingDirectory) =>
        $"\r\n{Dim}{Sanitize(workingDirectory)}{Reset}\r\n{Gold}${Reset} {Sanitize(command)}\r\n";

    /// <summary>
    /// One line of command output. stderr is colored rather than prefixed: the model's copy uses an
    /// "[err]" prefix because it reads plain text, but a person watching a terminal reads color
    /// faster and the prefix would just eat width.
    /// </summary>
    public static string Line(string line, bool isError) =>
        isError ? $"{Red}{Sanitize(line)}{Reset}\r\n" : $"{Sanitize(line)}\r\n";

    /// <summary>
    /// How the command ended. A non-zero exit and a kill are shown differently on purpose — one
    /// means the command ran and disagreed with you, the other means it never got to finish.
    /// </summary>
    public static string Footer(int? exitCode, string? killReason)
    {
        if (killReason != null)
            return $"{Gold}■ killed: {Sanitize(killReason)}{Reset}\r\n";
        if (exitCode == 0)
            return $"{Green}✓ exit 0{Reset}\r\n";
        return $"{Red}✗ exit {exitCode}{Reset}\r\n";
    }

    /// <summary>
    /// Strips control characters that would let a command's output drive the display rather than
    /// appear in it — a stray "clear screen" or cursor-home from a tool that emits VT even when it
    /// is not talking to a terminal would otherwise wipe the log someone is reading. Tabs are kept
    /// (they are alignment, and every compiler emits them); ESC is neutered so no sequence can
    /// start. Note this is display hygiene, not a security boundary: the same text has already
    /// gone to the model verbatim.
    /// </summary>
    private static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        int n = 0;
        foreach (var c in text)
        {
            if (c == '\t') { buffer[n++] = c; continue; }
            if (c == '\x1b') { buffer[n++] = '^'; continue; }
            if (char.IsControl(c)) continue;   // CR/LF included: this renders exactly one line
            buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }
}
