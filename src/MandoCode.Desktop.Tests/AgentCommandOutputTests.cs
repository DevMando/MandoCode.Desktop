using System.Text.RegularExpressions;
using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The agent output tab is a read-only mirror of what the agent runs. These cover the two host-side
/// pieces that decide what actually reaches xterm: how a command's lifecycle is rendered, and how
/// the buffer that survives until someone opens the panel is kept.
/// </summary>
public class AgentCommandOutputTests
{
    // \u001b, not \x1b: C# hex escapes are variable-length and greedily eat any hex
    // digit that follows, so "\x1b" next to one silently becomes a different character.
    private const char Esc = '\u001b';

    /// <summary>Complete SGR colour sequences, removed so what remains can be checked for damage.</summary>
    private static string StripSgr(string s) => Regex.Replace(s, @"\u001b\[[0-9;]*m", "");

    // ---- Formatting -------------------------------------------------------------

    [Fact]
    public void HeaderShowsTheCommandAndWhereItRuns()
    {
        // The folder matters: an agent runs in ITS project root, which is not necessarily the one
        // the user is looking at, and two agents share this panel.
        var header = AgentCommandFormat.Header("dotnet build", @"C:\src\project");

        Assert.Contains("dotnet build", StripSgr(header));
        Assert.Contains(@"C:\src\project", StripSgr(header));
    }

    [Fact]
    public void StderrIsDistinguishableFromStdout()
    {
        var stdout = AgentCommandFormat.Line("all good", isError: false);
        var stderr = AgentCommandFormat.Line("all good", isError: true);

        Assert.NotEqual(stdout, stderr);
        Assert.Equal("all good\r\n", stdout);           // no decoration on the common case
        Assert.Equal("all good", StripSgr(stderr).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void SuccessFailureAndKillReadDifferently()
    {
        // A non-zero exit means the command ran and disagreed with you; a kill means it never got
        // to finish. Someone watching has to be able to tell those apart at a glance.
        var ok = StripSgr(AgentCommandFormat.Footer(0, null));
        var failed = StripSgr(AgentCommandFormat.Footer(1, null));
        var killed = StripSgr(AgentCommandFormat.Footer(null, "idle 30s with no output"));

        Assert.Contains("exit 0", ok);
        Assert.Contains("exit 1", failed);
        Assert.Contains("killed", killed);
        Assert.Contains("idle 30s with no output", killed);
        Assert.NotEqual(ok, failed);
    }

    [Fact]
    public void CommandOutputCannotDriveTheDisplay()
    {
        // A tool that emits VT even when it is not talking to a terminal could otherwise clear the
        // screen or move the cursor, wiping the log someone is reading. The escape is defanged,
        // and the visible text is kept.
        var line = AgentCommandFormat.Line("\u001b[2Jwiped\u001b[Hagain", isError: false);

        Assert.DoesNotContain(Esc, (IEnumerable<char>)line);
        Assert.Contains("wiped", line);
        Assert.Contains("again", line);
    }

    [Fact]
    public void OneLineOfOutputStaysOneLine()
    {
        // Embedded newlines would let a single line forge the header/footer structure around it,
        // and would desynchronise the display from the line count the model was given.
        var line = AgentCommandFormat.Line("first\nsecond\rthird", isError: false);

        Assert.Equal("firstsecondthird\r\n", line);
    }

    [Fact]
    public void TabsSurviveBecauseTheyAreAlignment()
    {
        Assert.Equal("a\tb\r\n", AgentCommandFormat.Line("a\tb", isError: false));
    }

    // ---- Buffering --------------------------------------------------------------

    [Fact]
    public void RecordsForAViewThatIsNotOpenYet()
    {
        // The whole reason the log buffers: the terminal panel is built lazily, so an agent that
        // builds before the user opens it must still have something to show.
        var log = new AgentCommandLog();

        log.CommandStarted("git status", @"C:\src");
        log.CommandOutput("nothing to commit", isError: false);
        log.CommandFinished(0, null);

        var snapshot = StripSgr(log.Snapshot());
        Assert.Contains("git status", snapshot);
        Assert.Contains("nothing to commit", snapshot);
        Assert.Contains("exit 0", snapshot);
    }

    [Fact]
    public void LiveSubscribersSeeExactlyWhatIsBuffered()
    {
        var log = new AgentCommandLog();
        var live = "";
        log.Appended += text => live += text;

        log.CommandStarted("ls", @"C:\src");
        log.CommandOutput("file.txt", isError: false);
        log.CommandFinished(0, null);

        Assert.Equal(log.Snapshot(), live);
    }

    [Fact]
    public void ClearDropsWhatTheUserDismissed()
    {
        var log = new AgentCommandLog();
        log.CommandOutput("old news", isError: false);

        log.Clear();

        Assert.Equal("", log.Snapshot());
    }

    [Fact]
    public void BufferStaysBoundedAndKeepsTheNewestOutput()
    {
        var log = new AgentCommandLog();
        for (int i = 0; i < 4000; i++)
            log.CommandOutput($"line {i} " + new string('x', 100), isError: false);

        var snapshot = log.Snapshot();
        Assert.True(snapshot.Length <= AgentCommandLog.MaxBufferedChars,
            $"buffer grew to {snapshot.Length}");
        Assert.Contains("line 3999", snapshot);
        Assert.DoesNotContain("line 0 ", snapshot);
    }

    [Fact]
    public void TrimmingNeverLeavesHalfAnEscapeSequence()
    {
        // Cutting at an exact character count would routinely land mid-sequence, and half an SGR
        // code replayed into xterm colours everything after it until something else resets. The
        // buffer is trimmed to a line boundary to avoid that.
        var log = new AgentCommandLog();
        for (int i = 0; i < 4000; i++)
            log.CommandOutput($"error {i} " + new string('e', 100), isError: true);   // every line coloured

        Assert.DoesNotContain(Esc, (IEnumerable<char>)StripSgr(log.Snapshot()));
    }
}
