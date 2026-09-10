using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Handing a job to another agent without blocking on it. The design rests on one property — the
/// progress report is O(1) however long the job runs — so that is what most of these check.
/// </summary>
public class DelegationTests
{
    private static AgentEntry Entry(string key, string name, bool busy = true, bool cmd = false,
                                    int step = 0, int total = 0) =>
        new(key, name, $@"C:\src\{name}", name.ToLowerInvariant(), "qwen3:8b", busy, cmd, step, total);

    private static (DelegationRegistry Reg, Delegation D) Open() =>
        (new DelegationRegistry(), new DelegationRegistry().Open("a", "Sonic", "b", "Ninja", "build the site"));

    // ---- The inbox --------------------------------------------------------------

    [Fact]
    public void PostingTheSameIdReplacesRatherThanAccumulates()
    {
        // The whole cost argument. Ten minutes of progress reports must cost the same as one.
        var inbox = new AgentInbox();

        for (int i = 1; i <= 200; i++)
            inbox.Post(new InboxMessage("delegation:d1", "Ninja — working", $"step {i} of 200", DateTimeOffset.Now));

        var messages = inbox.Peek();
        Assert.Single(messages);
        Assert.Contains("step 200 of 200", messages[0].Body);
    }

    [Fact]
    public void DistinctMessagesAreCappedSoAProducerCannotGrowItWithoutBound()
    {
        var inbox = new AgentInbox();
        for (int i = 0; i < AgentInbox.MaxMessages + 10; i++)
            inbox.Post(new InboxMessage($"m{i}", "s", "b", DateTimeOffset.Now));

        Assert.Equal(AgentInbox.MaxMessages, inbox.Peek().Count);
        // Oldest dropped first: a stale report matters less than the newest event.
        Assert.DoesNotContain(inbox.Peek(), m => m.Id == "m0");
        Assert.Contains(inbox.Peek(), m => m.Id == $"m{AgentInbox.MaxMessages + 9}");
    }

    [Fact]
    public void DrainingEmptiesTheMailboxSoNothingIsDeliveredTwice()
    {
        var inbox = new AgentInbox();
        inbox.Post(new InboxMessage("m1", "s", "b", DateTimeOffset.Now));

        Assert.Single(inbox.Drain());
        Assert.Empty(inbox.Drain());
        Assert.False(inbox.HasMessages);
    }

    [Fact]
    public void TheDeliveredTextIsFramedAsSomethingThatHappenedNotSomethingSaid()
    {
        // Otherwise a delegation report reads as the user speaking, which is the same confusion the
        // agent-message envelope exists to prevent.
        var text = AgentInbox.Format(new[]
        {
            new InboxMessage("m1", "Ninja — finished", "the site is built", DateTimeOffset.Now)
        });

        Assert.Contains("While you were away", text);
        Assert.Contains("Background you now have", text);
        Assert.Contains("the site is built", text);
    }

    [Fact]
    public void AnEmptyMailboxFormatsToNothingAtAll()
    {
        // A turn with no news must not carry an empty header into the model's context.
        Assert.Equal("", AgentInbox.Format(Array.Empty<InboxMessage>()));
    }

    // ---- The digest -------------------------------------------------------------

    [Fact]
    public void AProgressDigestSaysWhatTheOtherAgentIsActuallyDoing()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Sonic", "b", "Ninja", "build the marketing site");

        var digest = DelegationRegistry.Digest(
            d, Entry("b", "Ninja", cmd: true, step: 4, total: 7), new[] { "npm install", "npm run build" });

        Assert.Contains("build the marketing site", digest.Body);
        Assert.Contains("step 4 of 7", digest.Body);
        Assert.Contains("command is running", digest.Body);
        Assert.Contains("npm run build", digest.Body);
    }

    [Fact]
    public void EveryProgressDigestSharesOneInboxIdSoItOverwrites()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Sonic", "b", "Ninja", "build it");

        var early = DelegationRegistry.Digest(d, Entry("b", "Ninja", step: 1, total: 7), Array.Empty<string>());
        var later = DelegationRegistry.Digest(d, Entry("b", "Ninja", step: 6, total: 7), Array.Empty<string>());

        Assert.Equal(early.Id, later.Id);
    }

    [Fact]
    public void AFinishedDigestCarriesTheResult()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Sonic", "b", "Ninja", "build it");
        var done = reg.Complete(d.Id, DelegationState.Done, "deployed to /dist")!;

        var digest = DelegationRegistry.Digest(done, Entry("b", "Ninja", busy: false), Array.Empty<string>());

        Assert.Contains("FINISHED", digest.Body);
        Assert.Contains("deployed to /dist", digest.Body);
    }

    [Fact]
    public void AFailedJobSaysSoRatherThanReadingAsStillWorking()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Sonic", "b", "Ninja", "build it");
        var failed = reg.Complete(d.Id, DelegationState.Failed, "busy")!;

        var digest = DelegationRegistry.Digest(failed, Entry("b", "Ninja"), Array.Empty<string>());

        Assert.Contains("DID NOT FINISH", digest.Body);
    }

    [Fact]
    public void AJobWhoseAgentClosedIsReportedStoppedNotRunning()
    {
        // A tab can close mid-job. Reporting it as "still working" would leave the delegating agent
        // waiting on something that can never finish.
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Sonic", "b", "Ninja", "build it");

        var digest = DelegationRegistry.Digest(d, peer: null, Array.Empty<string>());

        Assert.Contains("STOPPED", digest.Body);
        Assert.Contains("tab was closed", digest.Body);
    }

    // ---- The completion card ----------------------------------------------------

    [Fact]
    public void TheCardLeadsWithWhatHappenedNotWithTheBriefItWasGiven()
    {
        // Observed live: the card echoed back a paragraph of instructions the user had just watched
        // their agent compose, and never said what the job actually produced.
        var reg = new DelegationRegistry();
        var brief = "Mando would like you to restyle the Xbox Series X25 webpage to be Halo-themed. " +
                    "Please update the page's visual design to evoke the Halo franchise - think the " +
                    "Halo green/olive palette, Master Chief / Spartan aesthetic, sci-fi military styling.";
        var d = reg.Open("a", "Falchion", "b", "Ninja", brief);
        var done = reg.Complete(d.Id, DelegationState.Done,
            "Done - the Halo restyle is complete and live in the preview, with a UNSC top bar.")!;

        var card = DelegationRegistry.CompletionLine(done);

        Assert.Contains("Halo restyle is complete", card);   // the result is there
        Assert.DoesNotContain("Master Chief", card);         // the brief is not replayed
        Assert.True(card.Length < 300, $"card is {card.Length} chars - too long to scan");
    }

    [Fact]
    public void ALongBriefSurvivesOnlyAsAShortLabel()
    {
        // Enough to tell two outstanding jobs apart; not enough to be a wall of text.
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Falchion", "b", "Ninja", new string('x', 40) + " " + new string('y', 200));
        var done = reg.Complete(d.Id, DelegationState.Done, "finished it")!;

        var card = DelegationRegistry.CompletionLine(done);

        Assert.Contains("…", card);
        Assert.DoesNotContain(new string('y', 200), card);
    }

    [Fact]
    public void APreambleLikeDoneIsSkippedInFavourOfTheRealSentence()
    {
        // Models routinely open with a bare acknowledgement. A card showing only that says nothing.
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Falchion", "b", "Ninja", "build it");
        var done = reg.Complete(d.Id, DelegationState.Done,
            "Done.\n\nThe page now uses an olive palette with a UNSC dossier bar across the top.")!;

        var card = DelegationRegistry.CompletionLine(done);

        Assert.Contains("olive palette", card);
    }

    [Fact]
    public void AFailedCardCarriesTheReasonRatherThanTheBrief()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Falchion", "b", "Ninja", "build the site");
        var failed = reg.Complete(d.Id, DelegationState.Failed, "busy")!;

        var card = DelegationRegistry.CompletionLine(failed);

        Assert.Contains("did not finish", card);
        Assert.Contains("busy", card);
    }

    [Fact]
    public void AnEmptyResultStillProducesAReadableCard()
    {
        var reg = new DelegationRegistry();
        var d = reg.Open("a", "Falchion", "b", "Ninja", "build the site");
        var done = reg.Complete(d.Id, DelegationState.Done, "")!;

        var card = DelegationRegistry.CompletionLine(done);

        Assert.Contains("Ninja finished", card);
        Assert.DoesNotContain("—", card);   // no trailing dash with nothing after it
    }

    // ---- The registry -----------------------------------------------------------

    [Fact]
    public void AnAgentSeesOnlyTheJobsItHandedOut()
    {
        var reg = new DelegationRegistry();
        reg.Open("a", "Sonic", "b", "Ninja", "one");
        reg.Open("c", "Falchion", "b", "Ninja", "two");

        var mine = reg.OpenedBy("a");

        Assert.Single(mine);
        Assert.Equal("one", mine[0].Task);
    }

    [Fact]
    public void FinishedJobsAreSweptButRunningOnesSurvive()
    {
        var reg = new DelegationRegistry();
        var running = reg.Open("a", "Sonic", "b", "Ninja", "still going");
        var done = reg.Open("a", "Sonic", "b", "Ninja", "over");
        reg.Complete(done.Id, DelegationState.Done, "fine");

        reg.Sweep(TimeSpan.Zero);   // everything finished is old enough

        Assert.NotNull(reg.Get(running.Id));
        Assert.Null(reg.Get(done.Id));
    }
}
