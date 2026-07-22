using System.Diagnostics;
using System.Text;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Runs user shell commands (the `!cmd` / `/command` escape). The CLI's
/// ShellCommandHandler streams to the console; this version captures output and
/// appends it to the transcript. Same guardrails: output cap and a hard timeout.
/// </summary>
public sealed class ShellRunner
{
    private const int MaxOutputChars = 100_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly ProjectRootAccessor _projectRoot;
    private readonly TranscriptWriter _transcript;
    private readonly TranscriptHtmlBuilder _html;

    public ShellRunner(ProjectRootAccessor projectRoot, TranscriptWriter transcript, TranscriptHtmlBuilder html)
    {
        _projectRoot = projectRoot;
        _transcript = transcript;
        _html = html;
    }

    /// <summary>Returns (Failed, Output) so the caller can tell the MODEL what the user ran —
    /// `!` commands render only in the transcript, which the model never sees.</summary>
    public async Task<(bool Failed, string Output)> RunAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            _transcript.Append(_html.Warn("No command given. Usage: !<command>"));
            return (true, "");
        }

        var output = new StringBuilder();
        var failed = false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = _projectRoot.ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var gate = new object();

            void Collect(string? line)
            {
                if (line == null) return;
                lock (gate)
                {
                    if (output.Length < MaxOutputChars)
                        output.AppendLine(line);
                }
            }

            process.OutputDataReceived += (_, e) => Collect(e.Data);
            process.ErrorDataReceived += (_, e) => Collect(e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                lock (gate) output.AppendLine($"[command timed out after {Timeout.TotalSeconds:0}s and was terminated]");
                failed = true;
            }

            if (!failed && process.HasExited && process.ExitCode != 0)
            {
                failed = true;
                lock (gate) output.AppendLine($"[exit code {process.ExitCode}]");
            }

            string text;
            lock (gate)
            {
                if (output.Length >= MaxOutputChars)
                    output.AppendLine("… [output truncated]");
                text = output.ToString().TrimEnd();
            }
            if (text.Length == 0) text = "(no output)";

            _transcript.Append(_html.CommandOutputCard(command, text, failed));
            return (failed, text);
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Failed to run command: {ex.Message}"));
            return (true, ex.Message);
        }
    }
}
