namespace MandoCode.Desktop.Services;

/// <summary>
/// Periodic, best-effort nudge toward installing the mandocode CLI (the dotnet global tool
/// counterpart to this desktop app) — surfaced in the terminal, since that's the one place
/// someone would actually run it. Fires every <see cref="SessionsBetweenHints"/>-th shell
/// session (a persisted counter, so the cadence holds across app restarts too) for as long as
/// the tool stays missing, and falls silent the moment it's found installed. Every uncertain
/// case (can't read the counter, can't read PATH) resolves to "don't show": a missed hint costs
/// nothing, a repeated one costs trust.
/// </summary>
public static class MandoCliDetector
{
    // Tune to taste — a terminal can open many times in one sitting, so this wants to be
    // large enough that the hint reads as an occasional aside, not a recurring ad.
    private const int SessionsBetweenHints = 20;

    private static string CounterPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "cli-hint-counter");

    /// <summary>Call once per new shell tab. True only on the one session out of every
    /// <see cref="SessionsBetweenHints"/> that should actually show the hint — every other
    /// call just advances the counter (or does nothing at all once the tool is installed).</summary>
    public static bool ShouldShowHint()
    {
        try
        {
            if (IsInstalled()) return false;

            int count = ReadCounter() + 1;
            if (count < SessionsBetweenHints)
            {
                WriteCounter(count);
                return false;
            }

            WriteCounter(0);   // reset the cycle regardless of whether the caller can display it
            return true;
        }
        catch { return false; }   // uncertain -> stay quiet
    }

    /// <summary>True if the `mandocode` dotnet global tool is findable. Checks the fixed shim
    /// path `dotnet tool install --global` always writes to first (fast, no PATH parsing for
    /// the common case), then falls back to a PATH scan for anyone who installed it a
    /// different way (built from source, a custom --tool-path). Any failure here is treated
    /// as "installed" — see the class summary on the safe failure direction.</summary>
    public static bool IsInstalled()
    {
        try
        {
            var shim = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dotnet", "tools", "mandocode.exe");
            if (File.Exists(shim)) return true;

            var paths = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(paths)) return false;
            foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), "mandocode.exe"))) return true;
                }
                catch { /* malformed PATH entry — skip it */ }
            }
            return false;
        }
        catch { return true; }   // uncertain -> assume installed, stay quiet
    }

    private static int ReadCounter()
    {
        try { return int.TryParse(File.ReadAllText(CounterPath).Trim(), out var n) ? n : 0; }
        catch { return 0; }
    }

    private static void WriteCounter(int value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CounterPath)!);
            File.WriteAllText(CounterPath, value.ToString());
        }
        catch { /* best-effort — worst case the cadence drifts by a session or two */ }
    }
}
