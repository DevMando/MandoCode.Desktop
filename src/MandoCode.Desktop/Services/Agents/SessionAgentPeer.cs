using MandoCode.Desktop.ViewModels;

namespace MandoCode.Desktop.Services;

/// <summary>
/// A live <see cref="AgentSession"/> seen as something another agent can talk to.
///
/// <para>Ordinary delegated turns retain the target's tools and approvals. Read-only reviews run
/// under the shared engine's review scope, which blocks mutating tools for that peer turn.</para>
/// </summary>
public sealed class SessionAgentPeer : IAgentPeer
{
    private readonly ChatController _controller;
    private readonly BusyStateService _busy;

    public SessionAgentPeer(string key, ChatController controller, BusyStateService busy)
    {
        Key = key;
        _controller = controller;
        _busy = busy;
    }

    public string Key { get; }

    public bool IsBusy => _busy.IsBusy || _controller.IsProcessing;

    public async Task<PeerAnswer> AskAsync(string askedBy, string question, CancellationToken cancellationToken = default)
    {
        // The transcript line is written inside the turn itself, where it can be ordered against the
        // answer and skipped if the claim fails — see ChatController.AnswerPeerAsync.
        using (AgentCallChain.Enter(Key))
        {
            return await _controller.AnswerPeerAsync(askedBy, question, cancellationToken);
        }
    }
}
