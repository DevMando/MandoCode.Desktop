using MandoCode.Desktop.ViewModels;
using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class DeferredPlanCompletionTests
{
    private static PlanStepProposal[] Steps(params string[] descriptions)
        => [.. descriptions.Select(d => new PlanStepProposal(d, $"Do {d}."))];

    [Fact]
    public async Task NoPendingProposal_DoesNothing()
    {
        var completion = new DeferredPlanCompletion(new PlanHandoff());
        var followUps = 0;

        var result = await completion.CompleteAsync(
            CancellationToken.None,
            (_, _) => { followUps++; return Task.FromResult("unexpected"); });

        Assert.Equal(DeferredPlanCompletionResult.Empty, result);
        Assert.Equal(0, followUps);
    }

    [Fact]
    public async Task CancelledTurn_DropsProposalWithoutRunningIt()
    {
        var planRuns = 0;
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) =>
            {
                planRuns++;
                return Task.FromResult("unexpected");
            }
        };
        handoff.SetPendingProposal("goal", Steps("one"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new DeferredPlanCompletion(handoff).CompleteAsync(
            cts.Token,
            (_, _) => Task.FromResult("unexpected"));

        Assert.Equal(DeferredPlanCompletionResult.Empty, result);
        Assert.Equal(0, planRuns);
        Assert.False(handoff.HasPendingProposal);
    }

    [Fact]
    public async Task RejectedPlan_RunsExactlyOneDirectFollowUp()
    {
        var handoff = new PlanHandoff();
        DeferredPlanCompletion? completion = null;
        handoff.OnPlanRequested = (_, _) =>
        {
            completion!.Outcome = DeferredPlanOutcome.Rejected;
            return Task.FromResult("internal rejection directive");
        };
        completion = new DeferredPlanCompletion(handoff);
        handoff.SetPendingProposal("goal", Steps("one"));
        var prompts = new List<string>();

        var result = await completion.CompleteAsync(
            CancellationToken.None,
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult("direct answer");
            });

        Assert.Null(result.Manifest);
        Assert.Equal("direct answer", result.FollowUpResponse);
        Assert.Equal([DeferredPlanCompletion.RejectionFollowUpPrompt], prompts);
    }

    [Fact]
    public async Task RejectionFollowUp_CannotQueueOrRunAnotherPlan()
    {
        var planRuns = 0;
        var nestedFollowUps = 0;
        var handoff = new PlanHandoff();
        DeferredPlanCompletion? completion = null;
        handoff.OnPlanRequested = (_, _) =>
        {
            planRuns++;
            completion!.Outcome = DeferredPlanOutcome.Rejected;
            return Task.FromResult("rejected");
        };
        completion = new DeferredPlanCompletion(handoff);
        handoff.SetPendingProposal("first", Steps("one"));

        await completion.CompleteAsync(
            CancellationToken.None,
            async (_, ct) =>
            {
                // Simulate a model ignoring the direct-answer instruction and proposing again.
                handoff.SetPendingProposal("second", Steps("two"));
                var nested = await completion.CompleteAsync(
                    ct,
                    (_, _) =>
                    {
                        nestedFollowUps++;
                        return Task.FromResult("unexpected");
                    });
                Assert.Equal(DeferredPlanCompletionResult.Empty, nested);
                return "direct answer";
            });

        Assert.Equal(1, planRuns);
        Assert.Equal(0, nestedFollowUps);
        Assert.False(handoff.HasPendingProposal);
    }

    [Fact]
    public async Task CancelledPlan_DoesNotAppendItsInternalDirective()
    {
        var handoff = new PlanHandoff();
        DeferredPlanCompletion? completion = null;
        handoff.OnPlanRequested = (_, _) =>
        {
            completion!.Outcome = DeferredPlanOutcome.Cancelled;
            return Task.FromResult("internal cancellation directive");
        };
        completion = new DeferredPlanCompletion(handoff);
        handoff.SetPendingProposal("goal", Steps("one"));

        var result = await completion.CompleteAsync(
            CancellationToken.None,
            (_, _) => Task.FromResult("unexpected"));

        Assert.Equal(DeferredPlanCompletionResult.Empty, result);
    }

    [Fact]
    public async Task ExecutedPlan_ReturnsManifestWithoutFollowUp()
    {
        var followUps = 0;
        var handoff = new PlanHandoff();
        DeferredPlanCompletion? completion = null;
        handoff.OnPlanRequested = (plan, _) =>
        {
            completion!.Outcome = DeferredPlanOutcome.Executed;
            plan.Steps[0].Status = TaskStepStatus.Completed;
            plan.Steps[0].Result = "Implemented and verified.";
            plan.Status = TaskPlanStatus.Completed;
            return Task.FromResult("complete");
        };
        completion = new DeferredPlanCompletion(handoff);
        handoff.SetPendingProposal("goal", Steps("one"));

        var result = await completion.CompleteAsync(
            CancellationToken.None,
            (_, _) => { followUps++; return Task.FromResult("unexpected"); });

        Assert.NotNull(result.Manifest);
        Assert.Contains("1 of 1 steps completed", result.Manifest);
        Assert.Contains("Implemented and verified.", result.Manifest);
        Assert.Null(result.FollowUpResponse);
        Assert.Equal(0, followUps);
    }
}
