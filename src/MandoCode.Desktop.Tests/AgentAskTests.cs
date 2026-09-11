using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Asking another agent a real question, and handing one a job. These are the two cross-agent tools
/// that run a turn on someone else's model, so they are the only ones that can loop, collide with a
/// busy agent, or spend tokens the user did not ask for — most of what follows is about those three.
///
/// <para>Neither BLOCKS. Both hand off and return at once, so the asking agent stays available to
/// the user; the reply arrives through the inbox. The tests that pin "returns immediately" are the
/// point of the feature, not incidental detail — asking used to wait for the answer, which locked
/// the user out of their own agent for as long as the other one took.</para>
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

    /// <summary>The hand-off is captured rather than run, so a test sees exactly what WOULD be
    /// started without a background turn racing its assertions. <c>Started.Count</c> being 0 is how
    /// "nothing was set going" is proved.</summary>
    private sealed class Launches
    {
        public List<Delegation> All { get; } = new();
        public Delegation? Last => All.Count == 0 ? null : All[^1];
    }

    private static (AgentDirectoryTools Tools, FakePeer Peer, AgentDirectory Dir, Launches Started) Setup(
        bool busy = false, string answer = "done both")
    {
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja", busy, step: 4, total: 9), Entry(Self, "Sonic") });
        var peer = new FakePeer("k1", answer, busy);
        dir.RegisterPeer(peer);
        var started = new Launches();
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic", (d, _) => started.All.Add(d));
        return (tools, peer, dir, started);
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
        var (tools, _, _, _) = Setup();
        Assert.Contains("not handed any work", tools.CheckDelegations());
        await Task.CompletedTask;
    }

    [Fact]
    public void AskingReturnsAtOnceAndDoesNotWaitForTheAnswer()
    {
        // The whole point of the change. Asking used to await the far side's entire turn, so a
        // question that turned out to be ten minutes of work pinned the asking agent for ten
        // minutes and the user could not type into their own tab.
        var (tools, peer, _, started) = Setup(answer: "yes, both finished");

        var result = tools.AskAgent("Ninja", "have you finished the X and Y tasks?");

        Assert.Equal(0, peer.Asked);                     // nothing awaited on this thread
        Assert.Single(started.All);
        Assert.Equal("have you finished the X and Y tasks?", started.Last!.Task);
        Assert.Equal(DelegationKind.Question, started.Last!.Kind);
        Assert.DoesNotContain("yes, both finished", result);   // the answer cannot be known yet
    }

    [Fact]
    public void TheAskerIsToldNotToWaitOrGuessTheAnswer()
    {
        // The model is perfectly capable of "I'll wait for Ninja" — or worse, inventing what Ninja
        // is going to say. The tool result is the only place it learns neither is available.
        var (tools, _, _, _) = Setup();

        var result = tools.AskAgent("Ninja", "which port does the dev server use?");

        Assert.Contains("next turn", result);
        Assert.Contains("rather than waiting", result);
        Assert.Contains("do not guess", result);
    }

    [Fact]
    public void TheQuestionCarriesTheAskingAgentsName()
    {
        // The target shows this in its own transcript. Unattributed, a question would read as
        // something the user typed. The name now travels on the delegation rather than straight
        // into AskAsync, because the call that reaches the peer happens later, on another thread.
        var (tools, _, _, started) = Setup();

        tools.AskAgent("Ninja", "status?");

        Assert.Equal("Sonic", started.Last!.FromName);
        Assert.Equal("Ninja", started.Last!.ToName);
    }

    [Fact]
    public void ABusyAgentIsNotAskedAndTheCallerIsPointedAtTheReadTools()
    {
        // The busy case is the COMMON case — "have you finished" is asked precisely when the answer
        // might be no. A bare refusal would end the line of enquiry; naming the fallback keeps it
        // going with what can be read without interrupting anyone.
        //
        // This check MOVED. While asking blocked, the peer's atomic claim decided and this layer
        // only worded the refusal. Handing off means nobody is waiting to hear it, so busy has to
        // be caught before the hand-off — otherwise the model is told its question is on its way
        // and finds out it was not from a failure card much later.
        var (tools, peer, _, started) = Setup(busy: true);

        var result = tools.AskAgent("Ninja", "done yet?");

        Assert.Equal(0, peer.Asked);
        Assert.Empty(started.All);
        Assert.Contains("busy", result);
        Assert.Contains("step 4 of 9", result);
        Assert.Contains("read_agent_transcript", result);
    }

    [Fact]
    public async Task AnAgentThatGoesBusyAfterTheHandOffCompletesTheQuestionUnanswered()
    {
        // The race the atomic claim still exists for: the up-front check passes, and the agent takes
        // a turn before the background call lands. The far side must decline rather than run a
        // second turn on the same chat history — and because nobody is waiting on this path, the
        // refusal has to surface as a COMPLETED-but-unanswered delegation rather than a return value.
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja", step: 4, total: 9), Entry(Self, "Sonic") });
        var peer = new FakePeer("k1");
        dir.RegisterPeer(peer);

        PeerAnswer? outcome = null;
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic",
            (d, target) => { peer.IsBusy = true; outcome = target.AskAsync(d.FromName, d.Task).Result; });

        tools.AskAgent("Ninja", "done yet?");

        Assert.Equal(1, peer.Asked);
        Assert.False(outcome!.Answered);
        Assert.Equal("busy", outcome!.Text);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnUnknownAgentNamesTheOnesThatExist()
    {
        var (tools, peer, _, _) = Setup();

        var result = tools.AskAgent("Knuckles", "hello?");

        Assert.Equal(0, peer.Asked);
        Assert.Contains("Ninja", result);
    }

    [Fact]
    public async Task AskingYourselfIsRefused()
    {
        var (tools, peer, _, _) = Setup();
        Assert.Contains("is you", tools.AskAgent("Sonic", "what am I doing?"));
        Assert.Equal(0, peer.Asked);
    }

    [Fact]
    public async Task AClosedTabCannotBeAsked()
    {
        // The display snapshot can still name an agent whose tab has gone. Resolving must not
        // produce a reference to something that no longer exists.
        var (tools, peer, dir, _) = Setup();
        dir.RemovePeer("k1");

        var result = tools.AskAgent("Ninja", "still there?");

        Assert.Equal(0, peer.Asked);
        Assert.Contains("closed", result);
    }

    [Fact]
    public async Task AnAgentAlreadyInTheChainIsNotAskedAgain()
    {
        // Sonic asks Ninja; while answering, Ninja asks Sonic back. Without the guard the two
        // continue until the budget is gone.
        var (tools, peer, _, _) = Setup();

        using (AgentCallChain.Enter("k1"))
        {
            var result = tools.AskAgent("Ninja", "and you?");
            Assert.Equal(0, peer.Asked);
            Assert.Contains("loop", result);
        }
    }

    [Fact]
    public async Task AChainStopsAtTheDepthLimitEvenWithoutRepeatingAnAgent()
    {
        // A → B → C → D never repeats anyone, so the cycle check alone would let it run as far as
        // there are agents. The depth cap is what bounds the cost.
        var (tools, peer, _, _) = Setup();

        using (AgentCallChain.Enter("other-1"))
        using (AgentCallChain.Enter("other-2"))
        {
            var result = tools.AskAgent("Ninja", "one more?");
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
        var (tools, peer, _, _) = Setup();

        using (AgentCallChain.Enter("k1"))
        {
            await Task.Yield();
            await Task.Run(async () =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                var result = tools.AskAgent("Ninja", "still looping?");
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
        var (tools, peer, _, _) = Setup();

        var blocked = Task.Run(async () =>
        {
            using (AgentCallChain.Enter("other-1"))
            using (AgentCallChain.Enter("other-2"))
            {
                await Task.Delay(5).ConfigureAwait(false);
                return tools.AskAgent("Ninja", "deep chain");
            }
        });

        var allowed = Task.Run(async () =>
        {
            await Task.Delay(5).ConfigureAwait(false);
            return tools.AskAgent("Ninja", "fresh chain");
        });

        var results = await Task.WhenAll(blocked, allowed);

        Assert.Contains("limit", results[0]);        // the deep chain is stopped
        Assert.Contains("Asked Ninja", results[1]);  // the independent one is not
        Assert.Equal(0, peer.Asked);                 // handed off, not awaited here
    }

    [Fact]
    public async Task TheChainUnwindsSoLaterQuestionsAreNotBlocked()
    {
        // The guard is scoped to one chain. A question asked after an earlier chain finished must
        // start from a clean budget, or the first cross-agent call of a session would poison the rest.
        var (tools, peer, _, started) = Setup();

        using (AgentCallChain.Enter("other-1"))
        using (AgentCallChain.Enter("other-2")) { }

        var result = tools.AskAgent("Ninja", "now?");

        Assert.Equal(0, peer.Asked);       // handed off, not awaited here
        Assert.Single(started.All);
        Assert.Contains("Asked Ninja", result);
    }

    [Fact]
    public async Task TheLoopGuardSurvivesTheHandOffIntoABackgroundTurn()
    {
        // THE risk this change introduces. Asking used to await the far side inside the caller's
        // own async flow, so the chain was plainly still in scope. It now hands off to a
        // Task.Run in AgentSession.StartDelegation, and the guard only keeps working because
        // Task.Run captures ExecutionContext and an AsyncLocal rides along with it.
        //
        // If that ever stopped being true the guard would fail SILENTLY — no exception, no failing
        // test elsewhere, just agents talking in circles until the budget is gone. So this asserts
        // the chain is visible on the far side of exactly that boundary.
        var dir = new AgentDirectory();
        dir.Replace(new[] { Entry("k1", "Ninja"), Entry(Self, "Sonic") });
        dir.RegisterPeer(new FakePeer("k1"));

        string? seenInsideBackgroundTurn = null;
        var gate = new TaskCompletionSource();

        // Mirrors StartDelegation: fire-and-forget onto the pool, then Enter the target's key the
        // way SessionAgentPeer.AskAsync does before running the turn.
        var tools = new AgentDirectoryTools(dir, Self, () => "Sonic", (delegation, unusedPeer) =>
        {
            _ = unusedPeer;
            _ = Task.Run(() =>
            {
                using (AgentCallChain.Enter(delegation.ToKey))
                    seenInsideBackgroundTurn = AgentCallChain.Reject("upstream", "Upstream");
                gate.SetResult();
            });
        });

        // Sonic is answering Upstream when it asks Ninja — so "upstream" is already in the chain.
        using (AgentCallChain.Enter("upstream"))
            tools.AskAgent("Ninja", "what is the schema?");

        await gate.Task;

        // Inside the background turn the chain must be [upstream, k1] — so asking Upstream back
        // is refused as a loop. A null here means the chain was lost crossing the hand-off.
        Assert.NotNull(seenInsideBackgroundTurn);
        Assert.Contains("loop", seenInsideBackgroundTurn!);
    }

    // ---- how a finished question is worded back ----------------------------------

    private static Delegation Finished(DelegationKind kind, string task, string result) =>
        new("d1", Self, "Sonic", "k1", "Ninja", task, DateTimeOffset.Now.AddMinutes(-2),
            DelegationState.Done, result, DateTimeOffset.Now, kind);

    [Fact]
    public void AnAnsweredQuestionReadsAsAReplyNotAFinishedJob()
    {
        // Same machinery carries both, so without the kind a question asked in passing would come
        // back as "Ninja finished ..." — which reads as though work was done on the user's behalf.
        var line = DelegationRegistry.CompletionLine(
            Finished(DelegationKind.Question, "which port does the dev server use?", "It's on 5173."));

        Assert.Contains("replied", line);
        Assert.Contains("It's on 5173.", line);
        Assert.DoesNotContain("finished", line);
    }

    [Fact]
    public void AFinishedJobStillReadsAsFinished()
    {
        // The other half of the same guarantee: adding the question wording must not reword jobs.
        var line = DelegationRegistry.CompletionLine(
            Finished(DelegationKind.Job, "build the marketing site", "Done. Deployed to staging."));

        Assert.Contains("finished", line);
        Assert.DoesNotContain("replied", line);
    }

    [Fact]
    public void AnAnswerIsGivenMoreRoomOnTheCardThanAJobsOutcome()
    {
        // For a job the interesting thing is THAT it finished; the work is in the files. For a
        // question the reply IS the deliverable, so clipping it to a job's length would send the
        // user to the other agent's tab to read two sentences.
        var answer = string.Join(" ", Enumerable.Repeat("the schema uses snake_case throughout", 20));

        var asked = DelegationRegistry.CompletionLine(Finished(DelegationKind.Question, "schema?", answer));
        var job = DelegationRegistry.CompletionLine(Finished(DelegationKind.Job, "schema?", answer));

        Assert.True(asked.Length > job.Length, $"answer card {asked.Length} should exceed job card {job.Length}");
    }

    [Fact]
    public void TheInboxCarriesTheWholeAnswerEvenWhenTheCardTrimsIt()
    {
        // The card is for the user and is one line. The inbox copy is what the MODEL reads on its
        // next turn, so trimming there would make the clipped version the only one it ever sees.
        var answer = string.Join(" ", Enumerable.Repeat("a very specific detail that matters", 40));
        var d = Finished(DelegationKind.Question, "schema?", answer);

        var digest = DelegationRegistry.Digest(d, Entry("k1", "Ninja"), Array.Empty<string>());

        Assert.Contains(answer, digest.Body);
        Assert.Contains("replied", digest.Body);
        Assert.Contains("answered your question", digest.Subject);
    }

    [Fact]
    public void AQuestionStillWaitingReadsAsAnAnswerComingNotWorkInProgress()
    {
        var d = new Delegation("d1", Self, "Sonic", "k1", "Ninja", "schema?",
            DateTimeOffset.Now.AddMinutes(-1), Kind: DelegationKind.Question);

        var digest = DelegationRegistry.Digest(d, Entry("k1", "Ninja", busy: true, step: 2, total: 5),
                                               Array.Empty<string>());

        Assert.Contains("working out an answer", digest.Body);
        Assert.Contains("step 2 of 5", digest.Body);
    }
}
