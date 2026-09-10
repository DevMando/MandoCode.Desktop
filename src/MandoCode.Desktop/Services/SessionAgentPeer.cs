using MandoCode.Desktop.ViewModels;

namespace MandoCode.Desktop.Services;

/// <summary>
/// A live <see cref="AgentSession"/> seen as something another agent can talk to.
///
/// <para>A delegated turn is a FULL turn: the target answers with all of its own tools, exactly as
/// it would answer the user. It is not run with a reduced tool set, which means it can read, write
/// and run commands in its own project — and its own approval gates still stand in the way of the
/// dangerous ones, raising their dialogs in its own tab where they are attributable. The alternative
/// (a read-only delegated turn) would make the feature useless for the thing it is for: getting
/// another agent, who has the context, to actually help.</para>
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
