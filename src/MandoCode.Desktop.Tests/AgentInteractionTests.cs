using MandoCode.Desktop.Services;
using MandoCode.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class AgentInteractionTests
{
    private sealed class Peer(Func<CancellationToken, Task<PeerAnswer>> run) : IAgentPeer
    {
        public string Key => "target";
        public bool IsBusy { get; init; }
        public int Calls { get; private set; }
        public string? Brief { get; private set; }
        public Task<PeerAnswer> AskAsync(string askedBy, string question, CancellationToken cancellationToken = default)
        { Calls++; Brief = question; return run(cancellationToken); }
    }
    private static AgentEntry Entry(string key, string name) => new(key, name, "C:\\project", "project", "model", false, false, 0, 0);
    private static (AgentDirectory Directory, AgentDirectoryTools Tools, Peer Peer) Setup(Action<Delegation, IAgentPeer>? start = null, bool busy = false)
    {
        var directory = new AgentDirectory();
        directory.Replace([Entry("owner", "Owner"), Entry("target", "Target"), Entry("other", "Other")]);
        var peer = new Peer(_ => Task.FromResult(PeerAnswer.Ok("Answer"))) { IsBusy = busy };
        directory.RegisterPeer(peer);
        return (directory, new(directory, "owner", () => "Owner", start), peer);
    }

    [Theory]
    [InlineData("question", false)]
    [InlineData("review", true)]
    [InlineData("handoff", false)]
    public void BackgroundInteractionsReturnImmediatelyWithReadableIds(string kind, bool readOnly)
    {
        Delegation? started = null;
        var (_, tools, peer) = Setup((job, _) => started = job);
        var reply = kind switch { "review" => tools.RequestAgentReview("Target", "Check this"), "handoff" => tools.HandoffToAgent("Target", "Take over"), _ => tools.AskAgentAsync("Target", "Question") };
        Assert.NotNull(started); Assert.Equal("job1", started.Id); Assert.Contains("job1", reply);
        Assert.Equal(kind, started.Kind); Assert.Equal(readOnly, started.ReadOnly); Assert.Equal(0, peer.Calls);
    }

    [Fact]
    public void BusyTargetsRefuseWorkButCanReceiveMessagesWithoutStartingATurn()
    {
        var (directory, tools, peer) = Setup((_, _) => throw new Exception("Must not start"), busy: true);
        Assert.Contains("busy", tools.AskAgentAsync("Target", "Question"));
        Assert.Empty(directory.Delegations.OpenedBy("owner"));
        Assert.Contains("No reply requested", tools.SendAgentMessage("Target", "Information"));
        Assert.Equal("Information", Assert.Single(directory.InboxFor("target").Drain()).Body);
        Assert.Empty(directory.InboxFor("target").Drain()); Assert.Equal(0, peer.Calls);
    }

    [Fact]
    public void MessagesHaveHostAttributedTranscriptEvidenceForBothAgents()
    {
        var (directory, tools, _) = Setup();
        AgentCommunication? notice = null;
        directory.CommunicationPosted += n => notice = n;
        tools.SendAgentMessage("Target", "Useful context");
        Assert.NotNull(notice);
        Assert.Equal("owner", notice.FromKey); Assert.Equal("target", notice.ToKey);
        Assert.Equal("Owner", notice.FromName); Assert.Equal("Target", notice.ToName);
        Assert.Contains("no reply requested", notice.Title); Assert.Equal("Useful context", notice.Body);
    }

    [Fact]
    public void MissingSelfEmptyAndLoopedRequestsDoNotCreateJobs()
    {
        var (directory, tools, _) = Setup();
        tools.AskAgentAsync("Missing", "Question"); tools.RequestAgentReview("Owner", "Task"); tools.HandoffToAgent("Target", " ");
        using (AgentCallChain.Enter("target")) Assert.Contains("loop", tools.AskAgentAsync("Target", "Question"));
        Assert.Empty(directory.Delegations.OpenedBy("owner"));
    }

    [Fact]
    public async Task BlockingAliasReturnsTheAnswerAndForwardsCancellation()
    {
        var (directory, tools, _) = Setup();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        directory.RegisterPeer(new Peer(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return PeerAnswer.Ok("unused"); }));
        var pending = tools.AskAgentAndWait("Target", "Question", cts.Token);
        await entered.Task; cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(directory.Delegations.OpenedBy("owner"));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    [InlineData("delegate_to_agent")]
    [InlineData("open_browser_tab")]
    public async Task ReadOnlyReviewActuallyBlocksMutatingToolsThroughEngineMiddleware(string tool)
    {
        var registry = new DelegationRegistry();
        var job = registry.Open("owner", "Owner", "target", "Target", "Review", "review", true);
        var calls = 0;
        var function = AIFunctionFactory.Create(() => { calls++; return "Executed"; }, new() { Name = tool });
        var peer = new Peer(async _ =>
        {
            await Task.Yield(); Assert.True(CliAgentCallChain.IsReview);
            var context = new FunctionInvocationContext { Function = function, Arguments = new() };
            var result = await new AgentFunctionMiddleware(0).InterceptAsync(null!, context,
                (ctx, ct) => ctx.Function.InvokeAsync(ctx.Arguments, ct), default);
            Assert.Contains("read-only", result?.ToString());
            return PeerAnswer.Ok("Findings");
        });
        Assert.Equal(DelegationState.Done, (await AgentInteractionRunner.Run(registry, job, peer)).State);
        Assert.Equal(0, calls); Assert.False(CliAgentCallChain.IsReview);
    }

    [Fact]
    public async Task ReviewsAllowInspectionAndNormalDelegationCanUseItsTools()
    {
        foreach (var review in new[] { true, false })
        {
            var registry = new DelegationRegistry();
            var job = registry.Open("owner", "Owner", "target", "Target", "Task", readOnly: review);
            var calls = 0;
            var function = AIFunctionFactory.Create(() => { calls++; return "Content"; }, new() { Name = review ? "read_file_contents" : "custom_host_tool" });
            var peer = new Peer(async _ =>
            {
                var context = new FunctionInvocationContext { Function = function, Arguments = new() };
                await new AgentFunctionMiddleware(0).InterceptAsync(null!, context, (ctx, ct) => ctx.Function.InvokeAsync(ctx.Arguments, ct), default);
                return PeerAnswer.Ok("Done");
            });
            await AgentInteractionRunner.Run(registry, job, peer); Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task CancellationIsOwnerOnlyAndWaitsForAcknowledgementWithoutCancellingOtherJobs()
    {
        var registry = new DelegationRegistry();
        var job = registry.Open("owner", "Owner", "target", "Target", "Task");
        var second = registry.Open("owner", "Owner", "other", "Other", "Other task");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<PeerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = AgentInteractionRunner.Run(registry, job, new Peer(_ => { entered.SetResult(); return exit.Task; }));
        await entered.Task;
        Assert.False(registry.Cancel(job.Id, "stranger")); Assert.False(registry.Token(job.Id).IsCancellationRequested);
        Assert.True(registry.Cancel(job.Id, "owner")); Assert.True(registry.Token(job.Id).IsCancellationRequested);
        Assert.Equal(DelegationState.CancellationRequested, registry.Get(job.Id)!.State);
        Assert.False(registry.Token(second.Id).IsCancellationRequested);
        exit.SetResult(PeerAnswer.Ok("Late success"));
        Assert.Equal(DelegationState.Cancelled, (await run).State);
        Assert.Equal(DelegationState.Cancelled, registry.Complete(job.Id, DelegationState.Done, "Later success")!.State);
    }

    [Fact]
    public async Task WaitTimeoutAndCancellationLeaveTheJobRunning()
    {
        var (directory, tools, _) = Setup();
        var job = directory.Delegations.Open("owner", "Owner", "target", "Target", "Task");
        Assert.Contains("timed out", await tools.WaitForAgentJob(job.Id, 0));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.WaitForAgentJob(job.Id, 30, cts.Token));
        Assert.Equal(DelegationState.Running, directory.Delegations.Get(job.Id)!.State);
        Assert.False(directory.Delegations.Token(job.Id).IsCancellationRequested);
        directory.Delegations.Complete(job.Id, DelegationState.Done, "Result");
        Assert.Contains("Result", await tools.WaitForAgentJob(job.Id));
    }

    [Fact]
    public void UpdatesRequireJobMembershipAndAreQueuedOnceWithLatestValue()
    {
        var (directory, tools, _) = Setup();
        var job = directory.Delegations.Open("owner", "Owner", "target", "Target", "Task");
        Assert.False(directory.Delegations.UpdateJob(job.Id, "other", "Wrong agent"));
        tools.UpdateAgentJob(job.Id, "First"); tools.UpdateAgentJob(job.Id, "Latest");
        Assert.Equal("Latest", Assert.Single(directory.InboxFor("target").Drain()).Body);
        Assert.Contains("Latest", tools.CheckDelegations()); Assert.Contains("job1", tools.CheckDelegations());
        directory.Delegations.Complete(job.Id, DelegationState.Done, "Done");
        Assert.False(directory.Delegations.UpdateJob(job.Id, "owner", "Too late"));
    }

    [Fact]
    public async Task ClosingAnAgentCancelsItsJobsAndBusyRaceReportsFailure()
    {
        var (directory, _, peer) = Setup();
        var job = directory.Delegations.Open("owner", "Owner", "target", "Target", "Task");
        directory.RemovePeer("target");
        Assert.True(directory.Delegations.Token(job.Id).IsCancellationRequested);
        Assert.Equal(DelegationState.Cancelled, (await AgentInteractionRunner.Run(directory.Delegations, job, peer)).State);
        Assert.Equal(0, peer.Calls);
        var declined = directory.Delegations.Open("owner", "Owner", "target", "Target", "Task");
        var result = await AgentInteractionRunner.Run(directory.Delegations, declined, new Peer(_ => Task.FromResult(PeerAnswer.Busy())));
        Assert.Equal(DelegationState.Failed, result.State); Assert.Equal("busy", result.Result);
    }
}
