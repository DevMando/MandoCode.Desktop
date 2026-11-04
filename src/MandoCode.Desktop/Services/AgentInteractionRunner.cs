namespace MandoCode.Desktop.Services;

/// <summary>Execution boundary shared by Desktop background questions, jobs, reviews and handoffs.</summary>
public static class AgentInteractionRunner
{
    public static async Task<Delegation> Run(DelegationRegistry jobs, Delegation job, IAgentPeer target)
    {
        DelegationState state;
        string result;
        try
        {
            var ct = jobs.Token(job.Id);
            ct.ThrowIfCancellationRequested();
            // The shared engine's middleware checks this scope before ANY tool executes.
            using var review = MandoCode.Services.CliAgentCallChain.Enter(job.ToKey, review: job.ReadOnly);
            var brief = $"[{job.Kind} {job.Id}] {job.Task}";
            if (job.ReadOnly) brief += "\nRead-only review: inspect and report findings, suggested changes and validation gaps. Do not modify files or execute commands.";
            var answer = await target.AskAsync(job.FromName, brief, ct);
            state = ct.IsCancellationRequested ? DelegationState.Cancelled : answer.Answered ? DelegationState.Done : DelegationState.Failed;
            result = answer.Text;
        }
        catch (OperationCanceledException) { state = DelegationState.Cancelled; result = "Job cancelled. Changes already applied remain."; }
        catch (Exception ex) { state = DelegationState.Failed; result = ex.Message; }
        return jobs.Complete(job.Id, state, result) ?? job;
    }
}
