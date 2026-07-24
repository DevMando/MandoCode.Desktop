using System.Diagnostics;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Opens a file, folder, or URL with the OS default handler (ShellExecute). Centralizes the
/// <see cref="ProcessStartInfo"/> dance that was repeated at every "open this in Explorer / the
/// browser / its default app" call site. Returns the launch exception (null on success) so each
/// caller can surface its own message; a dead link or missing handler never crashes the app.
/// </summary>
public static class ShellOpen
{
    public static Exception? Try(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
