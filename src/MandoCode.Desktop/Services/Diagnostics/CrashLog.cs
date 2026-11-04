namespace MandoCode.Desktop.Services;

/// <summary>
/// Best-effort append to the same <c>crash.log</c> the App-level <c>UnhandledException</c>
/// handler writes to. Use it for a swallowed exception that is worth a diagnostic breadcrumb
/// but must never surface to the user or take the app down — an unexpected failure in an
/// otherwise recoverable path.
///
/// Not every empty catch belongs here: paths that throw as part of normal operation
/// (WebView2 host re-mapping, script execution during a teardown race) should stay silent so
/// this log carries signal, not noise. Logging is itself best-effort — a failure to write is
/// discarded rather than masking the original error.
/// </summary>
public static class CrashLog
{
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "crash.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTimeOffset.Now:O}] {context}: {ex.Message}\n{ex}\n\n");
        }
        catch { /* logging is best-effort — never mask the original failure */ }
    }
}
