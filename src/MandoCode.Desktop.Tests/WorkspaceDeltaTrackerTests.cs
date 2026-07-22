using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Encodes the manual workspace-notes test matrix as unit tests: external commit vs revert
/// disambiguation, branch switches, the silence guarantee (no notes when nothing happened,
/// no repeats), in-conversation commits staying unreported, the pending-capture race guard,
/// and touched-file tracking for content edits to already-dirty files.
/// </summary>
public sealed class WorkspaceDeltaTrackerTests
{
    private static GitBranchInfo Info(string branch = "main", string oid = "aaa111",
        params (string Path, string Kind)[] changes) =>
        new(branch,
            Dirty: changes.Length > 0,
            Conflicted: changes.Any(c => c.Kind == "!"),
            Ahead: 0, Behind: 0, Detached: false,
            Changes: changes.Select(c => new GitChangeEntry(c.Path, c.Kind)).ToList(),
            Oid: oid);

    /// <summary>First emit seeds the baseline and must say nothing.</summary>
    [Fact]
    public void FirstEmit_SeedsBaseline_Silently()
    {
        var t = new WorkspaceDeltaTracker();
        Assert.Empty(t.EmitDelta(Info(changes: ("a.cs", "M"))));
    }

    // Manual test #5: two sends back to back with nothing changed — no notes, no repeats.
    [Fact]
    public void NothingChanged_StaysSilent_AndNeverRepeats()
    {
        var t = new WorkspaceDeltaTracker();
        var state = Info(changes: ("a.cs", "M"));
        t.EmitDelta(state);                       // seed
        Assert.Empty(t.EmitDelta(state));
        Assert.Empty(t.EmitDelta(state));
    }

    // Manual test #3 (external terminal commit): changes gone + HEAD moved = COMMITTED.
    [Fact]
    public void ExternalCommit_ReportsCommitted()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1", changes: ("a.cs", "M")));   // seed dirty baseline

        var notes = t.EmitDelta(Info(oid: "oid2"));               // clean tree, new commit
        var note = Assert.Single(notes);
        Assert.Contains("COMMITTED", note);
        Assert.Contains("a.cs", note);
    }

    /// <summary>Changes gone but HEAD unchanged = the work was discarded, not saved.</summary>
    [Fact]
    public void ExternalRevert_ReportsReverted()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1", changes: ("a.cs", "M")));

        var notes = t.EmitDelta(Info(oid: "oid1"));               // clean tree, same commit
        var note = Assert.Single(notes);
        Assert.Contains("REVERTED", note);
        Assert.Contains("a.cs", note);
    }

    // Manual test #4: external branch switch is reported; resolved files get neutral
    // phrasing because a checkout moves HEAD without committing anything.
    [Fact]
    public void BranchSwitch_ReportsBranch_AndNeutralResolution()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(branch: "main", oid: "oid1", changes: ("a.cs", "M")));

        var notes = t.EmitDelta(Info(branch: "feature", oid: "oid2"));
        Assert.Equal(2, notes.Count);
        Assert.Contains("from 'main' to 'feature'", notes[0]);
        Assert.Contains("after the branch change", notes[1]);
        Assert.DoesNotContain("COMMITTED", notes[1]);
    }

    // Manual test #6: the agent commits mid-turn → baseline captured AFTER the turn already
    // reflects the clean tree → next send reports nothing.
    [Fact]
    public void InConversationCommit_ProducesNoNotes()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1", changes: ("a.cs", "M")));   // dirty before the turn

        t.MarkCapturePending();                                   // turn ends (agent committed)
        t.CaptureBaselineIfPending(Info(oid: "oid2"));            // post-turn snapshot: clean

        Assert.Empty(t.EmitDelta(Info(oid: "oid2")));
    }

    // Manual tests #1 and #7: while the post-turn capture is still pending, the baseline is
    // stale (predates the agent's own edits) — the tracker must stay silent, not guess.
    [Fact]
    public void PendingCapture_SuppressesEmit()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1"));                           // seed: clean

        t.MarkCapturePending();                                   // turn just ended, snapshot not landed
        Assert.Empty(t.EmitDelta(Info(oid: "oid1", changes: ("agent-edit.cs", "M"))));

        // Once the fresh snapshot lands, normal service resumes without misreporting.
        t.CaptureBaselineIfPending(Info(oid: "oid1", changes: ("agent-edit.cs", "M")));
        Assert.Empty(t.EmitDelta(Info(oid: "oid1", changes: ("agent-edit.cs", "M"))));
    }

    /// <summary>A file that becomes dirty between turns is an external change.</summary>
    [Fact]
    public void NewDirtyFile_ReportsChangedOnDisk()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1"));                           // seed: clean

        var notes = t.EmitDelta(Info(oid: "oid1", changes: ("b.cs", "M")));
        var note = Assert.Single(notes);
        Assert.StartsWith("Files changed on disk:", note);
        Assert.Contains("b.cs", note);
    }

    // The mando.txt bug: content edits to an ALREADY-dirty file don't move its status entry,
    // so only the watcher's touched-set can see them.
    [Fact]
    public void TouchedAlreadyDirtyFile_ReportsChangedOnDisk()
    {
        var t = new WorkspaceDeltaTracker();
        var dirty = Info(oid: "oid1", changes: ("mando.txt", "U"));
        t.EmitDelta(dirty);                                       // seed: already dirty

        t.RecordTouch("mando.txt");                               // external content edit
        var note = Assert.Single(t.EmitDelta(dirty));
        Assert.Contains("mando.txt", note);
        Assert.StartsWith("Files changed on disk:", note);

        // Touches are consumed with the emit — no repeat next turn.
        Assert.Empty(t.EmitDelta(dirty));
    }

    /// <summary>Touches recorded before a re-baseline belong to the old window and must not
    /// leak into the next one (the undo flow relies on this for its single-mention rule).</summary>
    [Fact]
    public void Touches_AreClearedByBaselineCapture()
    {
        var t = new WorkspaceDeltaTracker();
        var dirty = Info(oid: "oid1", changes: ("a.cs", "M"));
        t.EmitDelta(dirty);
        t.RecordTouch("a.cs");

        t.MarkCapturePending();                                   // e.g. the undo button fired
        t.CaptureBaselineIfPending(dirty);

        Assert.Empty(t.EmitDelta(dirty));
    }

    /// <summary>Big external change sets are capped, not dumped.</summary>
    [Fact]
    public void ManyFiles_AreCapped()
    {
        var t = new WorkspaceDeltaTracker();
        t.EmitDelta(Info(oid: "oid1"));

        var many = Enumerable.Range(1, 13).Select(i => ($"f{i:00}.cs", "M")).ToArray();
        var note = Assert.Single(t.EmitDelta(Info(oid: "oid1", changes: many)));
        Assert.Contains("(+3 more)", note);
    }

    /// <summary>Non-git folders never produce notes.</summary>
    [Fact]
    public void NullInfo_StaysSilent()
    {
        var t = new WorkspaceDeltaTracker();
        Assert.Empty(t.EmitDelta(null));
        Assert.Empty(t.EmitDelta(Info(changes: ("a.cs", "M"))));  // first real snapshot seeds
        Assert.Empty(t.EmitDelta(null));                          // repo vanished — still quiet
    }
}
