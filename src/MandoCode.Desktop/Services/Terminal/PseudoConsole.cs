using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MandoCode.Desktop.Services;

// A real terminal, the way Visual Studio / VS Code do it: a Windows pseudoconsole
// (ConPTY) driving a genuine shell process, with xterm.js rendering the output on
// the other side of a WebView2. ConPTY is what makes colors, cursor movement, and
// interactive TUI apps (vim, git log, progress bars) behave exactly like a real
// console — a plain redirected-stdout pipe cannot do that.
//
// This is hand-written P/Invoke rather than a NuGet package so the terminal carries
// zero new dependencies, matching the offline-first, submodule-pinned ethos of the app.
// It mirrors Microsoft's canonical GUIConsole.NET / MiniTerm ConPTY sample.

/// <summary>
/// Wraps the ConPTY handle (HPCON) plus the two pipes that carry bytes to and from
/// the hosted shell. The caller writes keystrokes to <see cref="WriteSide"/> and reads
/// shell output from <see cref="ReadSide"/>.
/// </summary>
internal sealed class PseudoConsole : IDisposable
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE — the attribute that binds a child process's
    // console to our HPCON (see Win32 CreateProcess extended startup info).
    internal const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    public IntPtr Handle { get; private set; }

    /// <summary>We write user keystrokes here; ConPTY feeds them to the shell as stdin.</summary>
    public SafeFileHandle WriteSide { get; }

    /// <summary>We read shell output (already VT-encoded by ConPTY) from here.</summary>
    public SafeFileHandle ReadSide { get; }

    // The pipe ends handed to the pseudoconsole itself. They must stay open for the
    // lifetime of the console, then be closed on dispose so the read side sees EOF.
    private readonly SafeFileHandle _consoleInputRead;
    private readonly SafeFileHandle _consoleOutputWrite;

    private PseudoConsole(IntPtr handle, SafeFileHandle writeSide, SafeFileHandle readSide,
                          SafeFileHandle consoleInputRead, SafeFileHandle consoleOutputWrite)
    {
        Handle = handle;
        WriteSide = writeSide;
        ReadSide = readSide;
        _consoleInputRead = consoleInputRead;
        _consoleOutputWrite = consoleOutputWrite;
    }

    public static PseudoConsole Create(short columns, short rows)
    {
        // Two anonymous pipes: one carries our keystrokes into the console, the other
        // carries the console's output back out to us.
        if (!Native.CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe (input) failed: " + Marshal.GetLastWin32Error());
        if (!Native.CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe (output) failed: " + Marshal.GetLastWin32Error());

        var size = new Native.COORD { X = Math.Max((short)1, columns), Y = Math.Max((short)1, rows) };
        int hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out IntPtr hpc);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");

        // We keep the write end of input and the read end of output; the other two ends
        // belong to the console and are retained here so the console keeps functioning.
        return new PseudoConsole(hpc, inputWrite, outputRead, inputRead, outputWrite);
    }

    public void Resize(short columns, short rows)
    {
        if (Handle == IntPtr.Zero) return;
        var size = new Native.COORD { X = Math.Max((short)1, columns), Y = Math.Max((short)1, rows) };
        Native.ResizePseudoConsole(Handle, size);
    }

    /// <summary>
    /// Close the console-owned pipe ends. Called once the child process has inherited
    /// them, so that when the shell exits our <see cref="ReadSide"/> observes EOF.
    /// </summary>
    public void ReleaseConsolePipeEnds()
    {
        _consoleInputRead.Dispose();
        _consoleOutputWrite.Dispose();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            // ClosePseudoConsole also terminates the attached process tree.
            Native.ClosePseudoConsole(Handle);
            Handle = IntPtr.Zero;
        }
        WriteSide.Dispose();
        ReadSide.Dispose();
        _consoleInputRead.Dispose();
        _consoleOutputWrite.Dispose();
    }
}

/// <summary>
/// A shell process launched with its console bound to a <see cref="PseudoConsole"/>,
/// via CreateProcess + an EXTENDED_STARTUPINFO_PRESENT attribute list.
/// </summary>
internal sealed class PseudoConsoleProcess : IDisposable
{
    private Native.PROCESS_INFORMATION _pi;
    private IntPtr _attributeList;

    public SafeProcessHandle ProcessHandle { get; }
    public uint ProcessId => _pi.dwProcessId;

    private PseudoConsoleProcess(Native.PROCESS_INFORMATION pi, IntPtr attributeList)
    {
        _pi = pi;
        _attributeList = attributeList;
        ProcessHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
    }

    public static PseudoConsoleProcess Start(PseudoConsole console, string commandLine, string? workingDirectory)
    {
        IntPtr attrList = BuildPseudoConsoleAttributeList(console.Handle);

        var startupInfo = new Native.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
        startupInfo.lpAttributeList = attrList;

        // CreateProcess mutates the command-line buffer, so it must be a writable copy.
        var cmd = new System.Text.StringBuilder(commandLine);

        bool ok = Native.CreateProcess(
            lpApplicationName: null,
            lpCommandLine: cmd,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: Native.EXTENDED_STARTUPINFO_PRESENT,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out Native.PROCESS_INFORMATION pi);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Native.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException($"CreateProcess failed for '{commandLine}' (Win32 {err}).");
        }

        return new PseudoConsoleProcess(pi, attrList);
    }

    private static IntPtr BuildPseudoConsoleAttributeList(IntPtr hpc)
    {
        // Two-call idiom: first call sizes the list, second fills it.
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        IntPtr attrList = Marshal.AllocHGlobal(size);
        if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());
        }

        if (!Native.UpdateProcThreadAttribute(
                attrList, 0, (IntPtr)PseudoConsole.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            Native.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());
        }

        return attrList;
    }

    public void Dispose()
    {
        if (_attributeList != IntPtr.Zero)
        {
            Native.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
        if (_pi.hThread != IntPtr.Zero)
        {
            Native.CloseHandle(_pi.hThread);
            _pi.hThread = IntPtr.Zero;
        }
        ProcessHandle.Dispose();
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    internal const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    // ConPTY — available since Windows 10 1809 (this app targets 10.0.19041+).
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
}
