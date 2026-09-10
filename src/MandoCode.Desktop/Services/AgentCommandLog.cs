using System.Text;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// One agent's shell-command activity, kept as terminal-ready text so the terminal panel can show
/// it live. Attached to that agent's <see cref="MandoCode.Services.AIService"/> as an
/// <see cref="ICommandOutputSink"/>; the engine calls into it as commands run.
///
/// <para>Why this buffers rather than only forwarding: the terminal panel is built lazily, on first
/// open. Without a buffer, an agent that ran a build before the user opened the panel would show an
/// empty tab — precisely the case the feature exists for. Whoever attaches replays
/// <see cref="Snapshot"/> first, then follows <see cref="Appended"/>.</para>
///
/// <para><b>Threading:</b> the engine raises output on the command's reader threads, so every
/// member here is under one lock and <see cref="Appended"/> fires on whatever thread produced the
/// output. Subscribers marshal to the UI thread themselves.</para>
/// </summary>
public sealed class AgentCommandLog : ICommandOutputSink
{
    /// <summary>
    /// Retained scrollback, in characters. Generous because it holds full build output that the
    /// model's own copy truncates, and bounded because a watch loop or a chatty test run would
    /// otherwise grow it without limit for as long as the agent lives.
    /// </summary>
    public const int MaxBufferedChars = 256 * 1024;

    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new();

    /// <summary>Fresh terminal text, already formatted. Raised on arbitrary threads.</summary>
    public event Action<string>? Appended;

    public void CommandStarted(string command, string workingDirectory) =>
        Append(AgentCommandFormat.Header(command, workingDirectory));

    public void CommandOutput(string line, bool isError) =>
        Append(AgentCommandFormat.Line(line, isError));

    public void CommandFinished(int? exitCode, string? killReason) =>
        Append(AgentCommandFormat.Footer(exitCode, killReason));

    /// <summary>Everything retained so far — replayed into a view that attaches late.</summary>
    public string Snapshot()
    {
        lock (_lock) { return _buffer.ToString(); }
    }

    /// <summary>Drops the retained scrollback. Used when the user closes the agent's output tab,
    /// so reopening it later starts clean rather than replaying what they dismissed.</summary>
    public void Clear()
    {
        lock (_lock) { _buffer.Clear(); }
    }

    private void Append(string text)
    {
        lock (_lock)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxBufferedChars) TrimOldest();
        }
        Appended?.Invoke(text);
    }

    /// <summary>
    /// Discards from the front, then advances to just past the next newline. Cutting at the exact
    /// character count would routinely land mid-escape-sequence, and half an SGR code replayed into
    /// xterm colors everything after it until something else resets — so the buffer is trimmed to a
    /// line boundary even though that drops a little more than strictly necessary.
    /// </summary>
    private void TrimOldest()
    {
        var excess = _buffer.Length - MaxBufferedChars;
        for (int i = excess; i < _buffer.Length; i++)
        {
            if (_buffer[i] == '\n')
            {
                _buffer.Remove(0, i + 1);
                return;
            }
        }
        // One line longer than the whole budget: no boundary to keep, so start over.
        _buffer.Clear();
    }
}
