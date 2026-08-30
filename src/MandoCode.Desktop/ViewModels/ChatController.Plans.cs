using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Desktop.ViewModels;

public sealed partial class ChatController
{
    /// <summary>Reports an unfinished plan without starting it. Resume always requires a command.</summary>
    private void ShowUnfinishedPlanNotice()
    {
        if (!_planRunners.SupportsResume) return;

        var saved = _planRunners.FindResumable(out var refusal);
        if (refusal != null)
        {
            _transcript.Append(_html.Warn(refusal));
            _transcript.Append(_html.Dim("Use /plan-discard to forget the incompatible checkpoint."));
            return;
        }

        if (saved == null) return;

        var outstanding = PlanCheckpointStore.OutstandingSteps(saved);
        if (outstanding == 0) return;

        _transcript.Append(_html.CheckpointCard(saved));
        _transcript.Append(_html.Dim("Resume or discard it here, or use /plan to inspect every step."));
    }

    private async Task HandlePlanCommandAsync(string action)
    {
        if (!string.IsNullOrWhiteSpace(action) && action is not "resume" and not "discard")
        {
            await ForcePlanAsync(action);
            return;
        }

        if (!_planRunners.SupportsResume)
        {
            _transcript.Append(_html.Warn("Plan resume requires the workflow planner."));
            _transcript.Append(_html.Dim("Enable it for this agent with: /config set planner workflow"));
            return;
        }

        if (action == "discard")
        {
            _planRunners.DiscardResumable();
            _transcript.Append(_html.Dim("Saved plan discarded for this agent."));
            return;
        }

        var saved = _planRunners.FindResumable(out var refusal);
        if (refusal != null)
        {
            _transcript.Append(_html.Warn(refusal));
            _transcript.Append(_html.Dim("Use /plan-discard to forget it."));
            return;
        }

        if (saved == null)
        {
            _transcript.Append(_html.Dim("No unfinished plan for this agent."));
            return;
        }

        var outstanding = PlanCheckpointStore.OutstandingSteps(saved);
        var done = saved.Steps.Count - outstanding;

        if (action != "resume")
        {
            _transcript.Append(_html.Info($"Unfinished plan: {PlanGoalPreview(saved.Goal)}"));
            _transcript.Append(_html.Dim($"{done} of {saved.Steps.Count} steps settled."));
            _transcript.Append(_html.CheckpointCard(saved));
            _transcript.Append(_html.PlanCard(PlanCheckpointStore.ToPlan(saved)));
            _transcript.Append(_html.Mono(string.Join("\n", saved.Steps.Select(step =>
            {
                var marker = step.Status switch
                {
                    TaskStepStatus.Completed => "done",
                    TaskStepStatus.Skipped => "skipped",
                    TaskStepStatus.Failed => "retry",
                    TaskStepStatus.InProgress => "interrupted",
                    _ => "pending"
                };
                return $"[{marker}] Step {step.Number}: {step.Description}";
            }))));
            _transcript.Append(_html.Dim("/plan-resume to continue, /plan-discard to forget it."));
            return;
        }

        if (!IsConnected || ModelError)
        {
            _transcript.Append(_html.Warn("Connect to a working model before resuming this plan."));
            _transcript.Append(_html.Dim("Use /retry, /model, or Settings, then run /plan-resume again."));
            return;
        }

        await ResumePlanAsync(saved);
    }

    /// <summary>
    /// Implements the deterministic <c>/plan &lt;goal&gt;</c> path. Proposal generation is isolated
    /// from the normal agent and can only call <c>propose_plan</c>; the resulting plan then enters
    /// the exact same review/edit/approval flow as a heuristic proposal.
    /// </summary>
    private async Task ForcePlanAsync(string goal)
    {
        if (!IsConnected || ModelError)
        {
            _transcript.Append(_html.Warn("Connect to a working model before creating a plan."));
            _transcript.Append(_html.Dim("Use /retry, /model, or Settings, then try /plan <goal> again."));
            return;
        }

        _requestCts = new CancellationTokenSource();
        var token = _requestCts.Token;
        _busy.Start("Creating plan...");

        try
        {
            _deferredPlans.Outcome = DeferredPlanOutcome.None;
            var proposal = await _ai.GeneratePlanAsync(goal, cancellationToken: token);
            var result = await _planHandoff.ProcessAsync(
                proposal.Goal, proposal.Steps, token, originalRequest: goal);

            if (_deferredPlans.Outcome == DeferredPlanOutcome.Executed &&
                !string.IsNullOrWhiteSpace(result))
            {
                _ai.AppendAssistantNote(result);
            }
            else if (_deferredPlans.Outcome == DeferredPlanOutcome.Rejected &&
                     !token.IsCancellationRequested)
            {
                // "One-shot it" still means what the approval button says even though this
                // command has no outer model turn waiting to receive the rejection result.
                var response = await _streamer.StreamAsync(
                    goal + "\n\n" + DeferredPlanCompletion.RejectionFollowUpPrompt, token);
                if (!string.IsNullOrEmpty(response)) _lastAiResponse = response;
                _planHandoff.ClearPendingProposal();
            }
        }
        catch (OperationCanceledException)
        {
            _transcript.Append(_html.Warn("Planning cancelled."));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Could not create plan: {ex.Message}"));
        }
        finally
        {
            _busy.Reset();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
            StateChanged?.Invoke();
        }
    }

    private async Task ResumePlanAsync(PlanRunState saved)
    {
        var plan = PlanCheckpointStore.ToPlan(saved);
        var runner = _planRunners.Current as WorkflowPlanRunner;
        if (runner == null)
        {
            _transcript.Append(_html.Error("The selected planner cannot resume workflow checkpoints."));
            return;
        }

        _transcript.Append(_html.Success(
            $"Resuming plan with {PlanCheckpointStore.OutstandingSteps(saved)} step(s) left..."));
        _ai.SetRequestContext(saved.Goal);

        _requestCts = new CancellationTokenSource();
        var token = _requestCts.Token;
        PlanProgressChanged?.Invoke(plan.CompletedStepsCount, plan.Steps.Count, true);
        _busy.Start("Resuming plan...");

        try
        {
            using var execution = _planHandoff.BeginResumedExecution(saved.FileOperations);
            var ui = _approvals.Ui
                ?? throw new InvalidOperationException("Approval UI not attached yet.");

            await foreach (var progressEvent in runner.ResumeAsync(plan, saved, token))
                await HandleProgressEventAsync(progressEvent, plan, ui, token);

            var manifest = PlanHandoff.BuildManifest(plan, _planHandoff.FileOperations);
            _ai.AppendAssistantNote(manifest);

            if (plan.Status == TaskPlanStatus.Completed)
                _transcript.Append(_html.Success("Resumed plan completed."));
            else if (plan.Status == TaskPlanStatus.CompletedWithIssues)
                _transcript.Append(_html.Warn("Resumed plan completed with skipped or failed steps."));
            else if (plan.Status == TaskPlanStatus.Cancelled)
                _transcript.Append(_html.Warn("Resumed plan was cancelled; its checkpoint remains available."));
            else
                _transcript.Append(_html.Error("Resumed plan finished with unresolved failures."));

            if (!string.IsNullOrWhiteSpace(plan.ExecutionSummary))
                _transcript.Append(_html.Dim(plan.ExecutionSummary));
        }
        catch (OperationCanceledException)
        {
            _transcript.Append(_html.Warn("Plan resume cancelled; completed progress remains checkpointed."));
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Could not resume plan: {ex.Message}"));
            _transcript.Append(_html.Dim("The checkpoint was kept. Use /plan-resume to try again."));
        }
        finally
        {
            PlanProgressChanged?.Invoke(plan.CompletedStepsCount, plan.Steps.Count, false);
            _busy.Reset();
            var oldCts = Interlocked.Exchange(ref _requestCts, null);
            oldCts?.Dispose();
            StateChanged?.Invoke();
        }
    }

    private static string PlanGoalPreview(string goal)
    {
        var oneLine = string.Join(" ", goal.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        return oneLine.Length <= 180 ? oneLine : oneLine[..177] + "...";
    }
}
