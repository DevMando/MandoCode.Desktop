using MandoCode.Services;

namespace MandoCode.Desktop.ViewModels;

/// <summary>The user's decision for a plan proposed during the current chat turn.</summary>
public enum DeferredPlanOutcome
{
    None,
    Executed,
    Rejected,
    Cancelled
}

/// <summary>The history additions produced after a deferred proposal is resolved.</summary>
public sealed record DeferredPlanCompletionResult(string? Manifest, string? FollowUpResponse)
{
    public static readonly DeferredPlanCompletionResult Empty = new(null, null);
}

/// <summary>
/// Completes a plan queued by <see cref="PlanHandoff"/> after the model's proposing turn drains.
/// Rejection is special: the user still expects the original request to be answered, so it gets
/// exactly one direct follow-up turn. Any plan proposed by that follow-up is discarded to prevent
/// a reject/propose loop.
/// </summary>
public sealed class DeferredPlanCompletion
{
    public const string RejectionFollowUpPrompt =
        "[system: the user reviewed your proposed plan and chose to skip stepwise " +
        "execution. Answer their original request directly now. Do not call propose_plan.]";

    private readonly PlanHandoff _planHandoff;
    private int _followUpDepth;

    public DeferredPlanCompletion(PlanHandoff planHandoff)
    {
        _planHandoff = planHandoff;
    }

    /// <summary>Set by the plan approval callback while the pending proposal is being resolved.</summary>
    public DeferredPlanOutcome Outcome { get; set; }

    public async Task<DeferredPlanCompletionResult> CompleteAsync(
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<string>> runFollowUpAsync)
    {
        if (!_planHandoff.HasPendingProposal)
            return DeferredPlanCompletionResult.Empty;

        // A direct follow-up is already answering a rejected plan. Running another proposal here
        // would contradict the user's choice and can create an unbounded proposal loop.
        if (_followUpDepth > 0)
        {
            _planHandoff.ClearPendingProposal();
            return DeferredPlanCompletionResult.Empty;
        }

        // Proposals belong only to the turn that created them. Never carry a cancelled proposal
        // into a later, unrelated request.
        if (cancellationToken.IsCancellationRequested)
        {
            _planHandoff.ClearPendingProposal();
            return DeferredPlanCompletionResult.Empty;
        }

        Outcome = DeferredPlanOutcome.None;
        var manifest = await _planHandoff.RunPendingPlanAsync(cancellationToken);

        if (Outcome == DeferredPlanOutcome.Cancelled)
            return DeferredPlanCompletionResult.Empty;

        if (Outcome != DeferredPlanOutcome.Rejected)
            return new DeferredPlanCompletionResult(manifest, null);

        _followUpDepth++;
        try
        {
            var response = await runFollowUpAsync(RejectionFollowUpPrompt, cancellationToken);
            return new DeferredPlanCompletionResult(null, response);
        }
        finally
        {
            // The rejected-plan answer is one-shot even if the model ignored the instruction and
            // called propose_plan again. Do not let that proposal leak into the next user turn.
            _planHandoff.ClearPendingProposal();
            _followUpDepth--;
        }
    }
}
