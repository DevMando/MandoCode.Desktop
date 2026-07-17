using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MandoCode.Desktop.Services;

/// <summary>Describes one kind of shell the terminal can spawn.</summary>
public sealed record ShellSpec(string Id, string DisplayName, string CommandLine, string Glyph);

/// <summary>
/// One live shell: a ConPTY-backed process, a background reader that surfaces raw
/// output bytes, and a writer for keystrokes. One instance backs one terminal tab.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly PseudoConsole _console;
    private readonly PseudoConsoleProcess _process;
    private readonly FileStream _reader;
    private readonly FileStream _writer;
    private readonly Thread _readThread;
    private RegisteredWaitHandle? _exitWait;
    private volatile bool _disposed;

    public string Id { get; }
    public ShellSpec Shell { get; }

    /// <summary>Raised on a background thread with a fresh slice of shell output (VT bytes).</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>Raised once when the shell process ends (marshal to UI before touching XAML).</summary>
    public event Action? Exited;

    public TerminalSession(string id, ShellSpec shell, string? workingDirectory, short columns, short rows)
    {
        Id = id;
        Shell = shell;

        _console = PseudoConsole.Create(columns, rows);
        _process = PseudoConsoleProcess.Start(_console, shell.CommandLine, workingDirectory);

        // Now that the child has inherited the console's pipe ends, drop our copies so
        // the read side reports EOF when the shell exits.
        _console.ReleaseConsolePipeEnds();

        _reader = new FileStream(_console.ReadSide, FileAccess.Read, bufferSize: 4096, isAsync: false);
        _writer = new FileStream(_console.WriteSide, FileAccess.Write, bufferSize: 4096, isAsync: false);

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"terminal-read-{id}",
        };
        _readThread.Start();

        // Fire Exited when the process object signals, independent of the read-loop EOF —
        // whichever happens first wins (RaiseExited guards against a double fire).
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            new WaitHandleForProcess(_process.ProcessHandle),
            (_, _) => RaiseExited(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                int read = _reader.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;   // pipe closed — shell has exited
                var slice = new byte[read];
                Buffer.BlockCopy(buffer, 0, slice, 0, read);
                OutputReceived?.Invoke(slice);
            }
        }
        catch (Exception) { /* pipe torn down during dispose — expected */ }
        RaiseExited();
    }

    /// <summary>Send user input (keystrokes / pasted text) to the shell as UTF-8.</summary>
    public void Write(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _writer.Write(bytes, 0, bytes.Length);
            _writer.Flush();
        }
        catch { /* shell gone */ }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed) return;
        try { _console.Resize(columns, rows); } catch { }
    }

    private int _exitRaised;
    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        try { Exited?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _exitWait?.Unregister(null); } catch { }
        // Closing the console kills the process tree and unblocks the read loop.
        try { _process.Dispose(); } catch { }
        try { _console.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
    }
}

/// <summary>
/// Minimal WaitHandle over a process handle, so ThreadPool.RegisterWaitForSingleObject
/// can signal us when the shell exits without polling.
/// </summary>
internal sealed class WaitHandleForProcess : WaitHandle
{
    public WaitHandleForProcess(SafeProcessHandle processHandle)
    {
        SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
    }
}

/// <summary>
/// Detects which shells are actually installed so the "+" menu only offers real options.
/// PowerShell 7 (pwsh) is preferred as the default when present, else Windows PowerShell.
/// </summary>
public static class ShellCatalog
{
    /// <summary>All shells found on this machine, in menu order (default first).</summary>
    public static IReadOnlyList<ShellSpec> Available()
    {
        var list = new List<ShellSpec>();

        string? pwsh = FindOnPath("pwsh.exe");
        if (pwsh != null)
            list.Add(new ShellSpec("pwsh", "PowerShell", Quote(pwsh) + " -NoLogo", ""));

        string winPs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs))
            list.Add(new ShellSpec("powershell", "Windows PowerShell", Quote(winPs) + " -NoLogo", ""));

        string cmd = Environment.GetEnvironmentVariable("ComSpec")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (File.Exists(cmd))
            list.Add(new ShellSpec("cmd", "Command Prompt", Quote(cmd), ""));

        foreach (var gitBash in new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        })
        {
            if (File.Exists(gitBash))
            {
                list.Add(new ShellSpec("gitbash", "Git Bash", Quote(gitBash) + " --login -i", ""));
                break;
            }
        }

        string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (File.Exists(wsl))
            list.Add(new ShellSpec("wsl", "WSL", Quote(wsl), ""));

        // Guaranteed fallback — cmd exists on every Windows install.
        if (list.Count == 0)
            list.Add(new ShellSpec("cmd", "Command Prompt", "cmd.exe", ""));

        return list;
    }

    public static ShellSpec Default() => Available()[0];

    public static ShellSpec? ById(string id) => Available().FirstOrDefault(s => s.Id == id);

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static string? FindOnPath(string exe)
    {
        string? paths = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(paths)) return null;
        foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
