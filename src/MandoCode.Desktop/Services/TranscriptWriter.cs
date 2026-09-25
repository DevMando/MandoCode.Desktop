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

    /// <summary>Raised after routine output for the current interaction is complete. The transcript
    /// surface uses it to collapse activity while leaving important notices and chat visible.</summary>
    public event Action? ActivityCompleted;

    /// <summary>Raised with the whole reply-so-far of the streaming turn <c>gen</c>. Not journaled:
    /// the live draft is replaced by real cards when the turn settles.</summary>
    public event Action<long, string>? LiveTextChanged;

    /// <summary>Raised when the live draft of turn <c>gen</c> becomes a provisional card (a tool call
    /// started). Not journaled until <see cref="LiveEnded"/> keeps it.</summary>
    public event Action<long, string>? LiveCardSealed;

    /// <summary>Raised once when turn <c>gen</c> settles: the draft goes away, and its provisional
    /// cards are kept (<c>true</c>) or removed because the final text disagreed with them.</summary>
    public event Action<long, bool>? LiveEnded;

    /// <summary>Raised for a block that is already on screen and only needs persisting — a kept
    /// provisional card. The journal listens; the transcript surface does not.</summary>
    public event Action<string>? BlockJournaled;

    public void Append(string html) => BlockAdded?.Invoke(html);

    public void UpdateLive(long gen, string text) => LiveTextChanged?.Invoke(gen, text);

    public void SealLive(long gen, string html) => LiveCardSealed?.Invoke(gen, html);

    public void EndLive(long gen, bool keepSealed) => LiveEnded?.Invoke(gen, keepSealed);

    public void Journal(string html) => BlockJournaled?.Invoke(html);

    public void Clear() => Cleared?.Invoke();

    public void CompleteActivity() => ActivityCompleted?.Invoke();
}
