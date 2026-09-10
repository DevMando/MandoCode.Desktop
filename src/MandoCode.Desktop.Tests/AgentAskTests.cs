using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Asking another agent a real question. This is the only cross-agent tool that runs a turn on
/// someone else's model, so it is the only one that can loop, collide with a busy agent, or spend
/// tokens the user did not ask for — everything here is about those three.
/// </summary>
public class AgentAskTests
{
    private const string Self = "self-key";

    private sealed class FakePeer : IAgentPeer
    {
        public FakePeer(string key, string answer = "done both", bool busy = false)
        {
            Key = key; Answer = answer; IsBusy = busy;
        }
        public string Key { get; }
        public string Answer { get; }
        public bool IsBusy { get; set; }
        public int Asked { get; private set; }
        public string? LastAskedBy { get; private set; }
        public string? LastQuestion { get; private set; }

        public Task<PeerAnswer> AskAsync(string askedBy, string question, CancellationToken cancellationToken = default)
        {
            Asked++; LastAskedBy = askedBy; LastQuestion = question;
            // Mirrors the real peer: it claims itself and reports back, rather than the caller
            // deciding from a flag it read a moment earlier.
            return Task.FromResult(IsBusy ? PeerAnswer.Busy() : PeerAnswer.Ok(Answer));
        }
    }

    private static AgentEntry Entry(string key, string name, bool busy = false, int step = 0, int total = 0) =>
        new(key, name, $@"C:\src\{name}", name.ToLowerInvariant(), "qwen3:8b", busy, false, step, total);

    private static (AgentDirectoryTools Tools, FakePeer Peer, AgentDirectory Dir) Setup(
        bool busy = false, string answer = "done both")
    {
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja", busy, step: 4, total: 9), Entry(Self, "Sonic") });
        var peer = new FakePeer("k1", answer, busy);
        dir.RegisterPeer(peer);
        return (new AgentDirectoryTools(dir, Self, () => "Sonic"), peer, dir);
    }

    [Fact]
    public async Task DelegatingReturnsImmediatelyWithoutWaitingForTheJob()
    {
        // The point of the whole feature: the delegating agent's turn ends at once, so the user is
        // not locked out of it while a long job runs.
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja"), Entry(Self, "Sonic") });
        var peer = new FakePeer("k1");
        dir.RegisterPeer(peer);
        Delegation? started = null;
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic", (d, _) => started = d);

        var result = tools.DelegateToAgent("Ninja", "build the marketing site");

        Assert.NotNull(started);
        Assert.Equal("build the marketing site", started!.Task);
        Assert.Equal(0, peer.Asked);                       // nothing awaited here
        Assert.Contains("You will be told when it finishes", result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ABusyAgentIsNotGivenAJobToSitOn()
    {
        // Queuing would tell the user their work was accepted while nothing had started, and hide
        // it behind work of unknown length.
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja", busy: true, step: 4, total: 9), Entry(Self, "Sonic") });
        dir.RegisterPeer(new FakePeer("k1", busy: true));
        var startedAnything = false;
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic", (_, _) => startedAnything = true);

        var result = tools.DelegateToAgent("Ninja", "build it");

        Assert.False(startedAnything);
        Assert.Contains("busy", result);
        Assert.Contains("step 4 of 9", result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CheckingDelegationsReportsEachJobWithoutAskingAnyone()
    {
        // This is what answers "how's the site coming?" while the other agent is mid-build — no
        // turn on either side, and it works precisely because the target is busy.
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja", busy: true, step: 2, total: 5), Entry(Self, "Sonic") });
        dir.RegisterPeer(new FakePeer("k1"));
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic", (_, _) => { });

        tools.DelegateToAgent("Ninja", "build the site");
        var status = tools.CheckDelegations();

        Assert.Contains("build the site", status);
        Assert.Contains("step 2 of 5", status);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WithNothingDelegatedTheReportSaysSoPlainly()
    {
        var (tools, _, _) = Setup();
        Assert.Contains("not handed any work", tools.CheckDelegations());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnIdleAgentIsAskedAndItsAnswerComesBack()
    {
        var (tools, peer, _) = Setup(answer: "yes, both finished");

        var result = await tools.AskAgent("Ninja", "have you finished the X and Y tasks?");

        Assert.Equal(1, peer.Asked);
        Assert.Equal("have you finished the X and Y tasks?", peer.LastQuestion);
        Assert.Contains("yes, both finished", result);
        Assert.Contains("Ninja replied", result);
    }

    [Fact]
    public async Task TheAnswerIsAttributedToTheAgentDoingTheAsking()
    {
        // The target shows this in its own transcript. Unattributed, a question would read as
        // something the user typed.
        var (tools, peer, _) = Setup();

        await tools.AskAgent("Ninja", "status?");

        Assert.Equal("Sonic", peer.LastAskedBy);
    }

    [Fact]
    public async Task ABusyAgentDeclinesAndTheCallerIsPointedAtTheReadTools()
    {
        // The busy case is the COMMON case — "have you finished" is asked precisely when the answer
        // might be no. A bare refusal would end the line of enquiry; naming the fallback keeps it
        // going with what can be read without interrupting anyone.
        //
        // Note the peer IS asked: the claim is what decides, not a flag read beforehand. Checking
        // first left a window where the agent could take a turn in between.
        var (tools, peer, _) = Setup(busy: true);

        var result = await tools.AskAgent("Ninja", "done yet?");

        Assert.Equal(1, peer.Asked);
        Assert.Contains("could not answer", result);
        Assert.Contains("step 4 of 9", result);
        Assert.Contains("read_agent_transcript", result);
    }

    [Fact]
    public async Task AnAgentThatBecomesBusyBetweenTheHintAndTheAskStillDeclinesCleanly()
    {
        // The race the atomic claim exists for: the directory says idle, and the agent takes a turn
        // before the question lands. The far side must decline rather than run a second turn on the
        // same chat history, and the caller must still get the useful refusal.
        var (tools, peer, _) = Setup(busy: false);
        peer.IsBusy = true;   // as if a turn started in the gap

        var result = await tools.AskAgent("Ninja", "done yet?");

        Assert.Contains("could not answer", result);
        Assert.Contains("read_agent_transcript", result);
    }

    [Fact]
    public async Task AnUnknownAgentNamesTheOnesThatExist()
    {
        var (tools, peer, _) = Setup();

        var result = await tools.AskAgent("Knuckles", "hello?");

        Assert.Equal(0, peer.Asked);
        Assert.Contains("Ninja", result);
    }

    [Fact]
    public async Task AskingYourselfIsRefused()
    {
        var (tools, peer, _) = Setup();
        Assert.Contains("is you", await tools.AskAgent("Sonic", "what am I doing?"));
        Assert.Equal(0, peer.Asked);
    }

    [Fact]
    public async Task AClosedTabCannotBeAsked()
    {
        // The display snapshot can still name an agent whose tab has gone. Resolving must not
        // produce a reference to something that no longer exists.
        var (tools, peer, dir) = Setup();
        dir.RemovePeer("k1");

        var result = await tools.AskAgent("Ninja", "still there?");

        Assert.Equal(0, peer.Asked);
        Assert.Contains("closed", result);
    }

    [Fact]
    public async Task AnAgentAlreadyInTheChainIsNotAskedAgain()
    {
        // Sonic asks Ninja; while answering, Ninja asks Sonic back. Without the guard the two
        // continue until the budget is gone.
        var (tools, peer, _) = Setup();

        using (AgentCallChain.Enter("k1"))
        {
            var result = await tools.AskAgent("Ninja", "and you?");
            Assert.Equal(0, peer.Asked);
            Assert.Contains("loop", result);
        }
    }

    [Fact]
    public async Task AChainStopsAtTheDepthLimitEvenWithoutRepeatingAnAgent()
    {
        // A → B → C → D never repeats anyone, so the cycle check alone would let it run as far as
        // there are agents. The depth cap is what bounds the cost.
        var (tools, peer, _) = Setup();

        using (AgentCallChain.Enter("other-1"))
        using (AgentCallChain.Enter("other-2"))
        {
            var result = await tools.AskAgent("Ninja", "one more?");
            Assert.Equal(0, peer.Asked);
            Assert.Contains("limit", result);
        }
    }

    [Fact]
    public async Task TheGuardSurvivesTheAwaitsAndThreadHopsOfARealCall()
    {
        // The chain is carried in an AsyncLocal, and the check happens deep inside the engine's
        // tool-invocation machinery rather than next to the Enter() that set it. If execution
        // context did not flow across those boundaries the guard would stop guarding SILENTLY —
        // no exception, no failing test, just two agents talking until the budget is gone.
        //
        // So this deliberately crosses the boundaries a real call crosses: an await, a thread-pool
        // hop, and a continuation that does not capture context.
        var (tools, peer, _) = Setup();

        using (AgentCallChain.Enter("k1"))
        {
            await Task.Yield();
            await Task.Run(async () =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                var result = await tools.AskAgent("Ninja", "still looping?").ConfigureAwait(false);
                Assert.Contains("loop", result);
            }).ConfigureAwait(false);
        }

        Assert.Equal(0, peer.Asked);
    }

    [Fact]
    public async Task TwoChainsRunningAtOnceDoNotSpendEachOthersBudget()
    {
        // Two conversations can ask questions at the same moment. A shared field would let one
        // chain's depth block the other's first question — the reason this is an AsyncLocal and not
        // a static counter. Verified by running both concurrently rather than in sequence.
        var (tools, peer, _) = Setup();

        var blocked = Task.Run(async () =>
        {
            using (AgentCallChain.Enter("other-1"))
            using (AgentCallChain.Enter("other-2"))
            {
                await Task.Delay(5).ConfigureAwait(false);
                return await tools.AskAgent("Ninja", "deep chain").ConfigureAwait(false);
            }
        });

        var allowed = Task.Run(async () =>
        {
            await Task.Delay(5).ConfigureAwait(false);
            return await tools.AskAgent("Ninja", "fresh chain").ConfigureAwait(false);
        });

        var results = await Task.WhenAll(blocked, allowed);

        Assert.Contains("limit", results[0]);          // the deep chain is stopped
        Assert.Contains("Ninja replied", results[1]);  // the independent one is not
        Assert.Equal(1, peer.Asked);
    }

    [Fact]
    public async Task TheChainUnwindsSoLaterQuestionsAreNotBlocked()
    {
        // The guard is scoped to one chain. A question asked after an earlier chain finished must
        // start from a clean budget, or the first cross-agent call of a session would poison the rest.
        var (tools, peer, _) = Setup();

        using (AgentCallChain.Enter("other-1"))
        using (AgentCallChain.Enter("other-2")) { }

        var result = await tools.AskAgent("Ninja", "now?");

        Assert.Equal(1, peer.Asked);
        Assert.Contains("Ninja replied", result);
    }
}
