using System.ComponentModel;

namespace MandoCode.Desktop.Services;

public sealed partial class AgentDirectoryTools
{
    [Description("BLOCK this turn until another agent answers a question needed immediately. Prefer ask_agent_async when the answer can arrive later and the user should stay free to chat.")]
    public Task<string> AskAgentAndWait(string name, string question, CancellationToken cancellationToken = default) => AskAgent(name, question, cancellationToken);

    [Description("Ask a question in the BACKGROUND, returning a job ID immediately. Finish this turn and let the user keep chatting; the result arrives in a completion notice and next-turn inbox context. Do not wait unless the answer is necessary to continue.")]
    public string AskAgentAsync(string name, string question) => StartInteraction(name, question, "question");

    [Description("Send information without requesting a reply or starting a model turn. Queues for the recipient's next turn, even while busy. Does not interrupt current work.")]
    public string SendAgentMessage(string name, string message)
    {
        var agent = _directory.Resolve(name);
        if (agent == null) return NotFound(name);
        if (agent.Key == _selfKey) return "That agent is you.";
        if (_directory.Peer(agent.Key) == null) return "That agent has closed.";
        if (string.IsNullOrWhiteSpace(message)) return "Write a message first.";
        _directory.InboxFor(agent.Key).Post(new InboxMessage(Guid.NewGuid().ToString("N"), $"Message from {_selfName}", message, DateTimeOffset.Now));
        _directory.AnnounceCommunication(new(_selfKey, agent.Key, _selfName, agent.Name, "Message queued · no reply requested", message));
        return $"Message queued for {agent.Name}'s next turn. No reply requested.";
    }

    [Description("Request a BACKGROUND read-only review. The host blocks writes, commands, plans, delegation and other mutating tools during the review. Include the scope and acceptance criteria. Returns a job ID; the result arrives later.")]
    public string RequestAgentReview(string name, string task) => StartInteraction(name, task, "review", true);

    [Description("Transfer remaining work to another agent in the BACKGROUND. Include the goal, decisions, current state, files and acceptance criteria. After submitting, stop duplicating the task and tell the user which agent is taking over. This does not switch tabs.")]
    public string HandoffToAgent(string name, string task) => StartInteraction(name, task, "handoff");

    [Description("Post a progress update or additional instructions for a running job. Only the requester and assigned agent can update it. Instructions are queued for the recipient's next turn and do not rewrite its active request; cancel/reassign if current work must stop.")]
    public string UpdateAgentJob(string jobId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Write an update first.";
        if (!_directory.Delegations.UpdateJob(jobId, _selfKey, message)) return "Job not found, no longer running, or belongs to other agents.";
        var job = _directory.Delegations.Get(jobId)!;
        var recipient = job.FromKey == _selfKey ? job.ToKey : job.FromKey;
        _directory.InboxFor(recipient).Post(new InboxMessage($"update:{jobId}", $"{jobId}: update from {_selfName}", message, DateTimeOffset.Now));
        _directory.AnnounceCommunication(new(_selfKey, recipient, _selfName,
            job.FromKey == _selfKey ? job.ToName : job.FromName, $"{jobId} · Update queued for next turn", message));
        return $"Updated {jobId}. Queued for the recipient's next turn.";
    }

    [Description("Request cancellation of a background job YOU assigned. Cancels only that job's turn, not unrelated work. check_delegations reports acknowledgement; completed file changes are preserved.")]
    public string CancelAgentJob(string jobId) => _directory.Delegations.Cancel(jobId, _selfKey)
        ? $"Cancellation requested for {jobId}. Changes already applied remain."
        : "Job not found, no longer running, or not assigned by you.";

    [Description("Explicitly BLOCK waiting for a background job you assigned, for up to 120 seconds. Timing out or cancelling this wait does not cancel the job. Prefer check_delegations for non-blocking progress; avoid polling loops.")]
    public async Task<string> WaitForAgentJob(string jobId, int timeoutSeconds = 30, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, 120));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = _directory.Delegations.Get(jobId);
            if (job == null || job.FromKey != _selfKey) return "Job not found or not assigned by you.";
            if (job.State is DelegationState.Done or DelegationState.Failed or DelegationState.Cancelled)
                return $"{job.Id}: {job.State}\n{job.Result}";
            if (timer.Elapsed >= timeout) return $"{job.Id}: {job.State}. Only the wait timed out; the job was not cancelled.";
            await Task.Delay(150, cancellationToken);
        }
    }

    private string StartInteraction(string name, string task, string kind, bool readOnly = false)
    {
        var agent = _directory.Resolve(name);
        if (agent == null) return NotFound(name);
        if (agent.Key == _selfKey) return "That agent is you.";
        if (string.IsNullOrWhiteSpace(task)) return "Describe the request first.";
        var peer = _directory.Peer(agent.Key);
        if (peer == null) return "That agent has closed.";
        if (peer.IsBusy) return $"{agent.Name} is busy. Try later, read its status/transcript, or send_agent_message to queue information.";
        if (AgentCallChain.Reject(agent.Key, agent.Name) is { } rejection) return rejection;
        var job = _directory.Delegations.Open(_selfKey, _selfName, agent.Key, agent.Name, task, kind, readOnly);
        _start(job, peer);
        return $"{job.Id}: {kind} submitted to {agent.Name}. The target will attempt to claim its turn. Finish this turn and let the user continue chatting; completion arrives automatically.";
    }
}
