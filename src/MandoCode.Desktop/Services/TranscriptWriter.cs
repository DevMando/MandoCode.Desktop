namespace MandoCode.Desktop.Services;

/// <summary>
/// The WinUI replacement for imperative AnsiConsole scrollback writes. Anything that
/// wants to put a block in the transcript (chat controller, approval service, shell
/// runner) appends an HTML fragment here; MainWindow subscribes and pushes fragments
/// into the WebView2 transcript on the UI thread.
/// </summary>
public sealed class TranscriptWriter
{
    /// <summary>Raised (possibly from a background thread) with an HTML fragment to append.</summary>
    public event Action<string>? BlockAdded;

    /// <summary>Raised when the transcript should be cleared (e.g. /clear).</summary>
    public event Action? Cleared;

    public void Append(string html) => BlockAdded?.Invoke(html);

    public void Clear() => Cleared?.Invoke();
}
