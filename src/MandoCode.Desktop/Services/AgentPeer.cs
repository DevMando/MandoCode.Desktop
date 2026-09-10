namespace MandoCode.Desktop.Services;

/// <summary>
/// What one agent can do TO another. Kept as an interface, and kept free of any UI type, so the
/// cross-agent tools can be exercised without a live model or a window.
/// </summary>
/// <summary>
/// The outcome of putting a question to another agent. A result rather than a formatted string so
/// the peer reports WHAT HAPPENED and the tool decides HOW TO SAY IT — otherwise the wording of
/// "that agent is busy" has to exist in two places, and the two drift.
/// </summary>
/// <param name="Answered">False when no turn ran; <paramref name="Text"/> is then a bare reason.</param>
public sealed record PeerAnswer(bool Answered, string Text)
{
    public static PeerAnswer Ok(string text) => new(true, text);
    public static PeerAnswer Busy() => new(false, "busy");
    public static PeerAnswer Failed(string reason) => new(false, reason);
}

public interface IAgentPeer
{
    string Key { get; }

    /// <summary>
    /// A best-effort hint for ordering the caller's options — it can be stale the instant it is
    /// read. It is NOT the safety check: <see cref="AskAsync"/> claims the agent atomically and
    /// reports back if it could not, which is the answer that can be trusted.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Runs a real turn on this agent's model and returns what it said. The asking agent's name is
    /// passed so the question can be attributed in this agent's own transcript — one that appeared
    /// from nowhere would look like the user typed it.
    /// </summary>
    Task<PeerAnswer> AskAsync(string askedBy, string question, CancellationToken cancellationToken = default);
}

/// <summary>
/// Guards against agents talking in circles. Sonic asks Knuckles, Knuckles asks Sonic, and without
/// this the two would happily continue until the token budget is gone.
///
/// <para>Two separate limits, because they catch different failures. The KEY SET catches a true
/// cycle — an agent already in the current chain being asked again — which is the fast, obvious
/// case. The DEPTH cap catches a chain that never repeats an agent but keeps going anyway, which a
/// key set alone would let run as long as there are agents.</para>
///
/// <para><see cref="AsyncLocal{T}"/> rather than a field: the chain is a property of one call
/// sequence, not of the process. Two unrelated conversations asking questions at the same time must
/// not consume each other's budget, and AsyncLocal flows through the awaits that make up a chain
/// while staying invisible to everything alongside it.</para>
/// </summary>
public static class AgentCallChain
{
    /// <summary>How many agents deep a single chain may go. Two is deliberate and low: A asks B,
    /// and B may consult C on A's behalf. Anything past that is a conversation the user did not
    /// ask for and is paying for by the turn.</summary>
    public const int MaxDepth = 2;

    private static readonly AsyncLocal<ImmutableChain?> Current = new();

    private sealed record ImmutableChain(IReadOnlyList<string> Keys)
    {
        public int Depth => Keys.Count;
        public bool Contains(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
        public ImmutableChain With(string key) => new(Keys.Append(key).ToList());
    }

    /// <summary>Why a call cannot proceed, or null when it can.</summary>
    public static string? Reject(string targetKey, string targetName)
    {
        var chain = Current.Value;
        if (chain == null) return null;

        if (chain.Contains(targetKey))
            return $"{targetName} is already part of this chain of questions — answering would loop. " +
                   "Answer from what you already have, or tell the user what is missing.";

        if (chain.Depth >= MaxDepth)
            return $"This question is already {chain.Depth} agents deep, which is the limit. " +
                   $"Do not ask {targetName}; answer from what you have.";

        return null;
    }

    /// <summary>Marks an agent as part of the current chain for the duration of the call.</summary>
    public static IDisposable Enter(string key)
    {
        var previous = Current.Value;
        Current.Value = (previous ?? new ImmutableChain(Array.Empty<string>())).With(key);
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ImmutableChain? _previous;
        private bool _done;
        public Scope(ImmutableChain? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Current.Value = _previous;
        }
    }
}
