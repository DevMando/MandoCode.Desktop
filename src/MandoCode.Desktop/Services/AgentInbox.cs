namespace MandoCode.Desktop.Services;

/// <summary>One thing that happened while an agent was not looking.</summary>
/// <param name="Id">Stable across updates — posting the same id again REPLACES the entry rather
/// than adding one. This is what keeps a long-running report O(1) instead of a growing log.</param>
public sealed record InboxMessage(string Id, string Subject, string Body, DateTimeOffset At);

/// <summary>
/// One agent's mailbox: what happened elsewhere that it should know about.
///
/// <para>Exists because agents are turn-based. Nothing can "tell" an idle agent something — it only
/// runs when prompted — so events that arrive between turns have to wait somewhere. They land here
/// and are folded into the next message's preamble, the same way imported snapshots already ride
/// along.</para>
///
/// <para><b>Upsert, not append.</b> A delegation posts progress under one id for its whole life, so
/// a job that runs for ten minutes costs the same context as one that runs for ten seconds. An
/// append-only feed would grow with the other agent's work and be paid for on every turn afterwards
/// — which is the transcript-copying problem arriving in instalments.</para>
///
/// <para><b>Threading:</b> written from background delegation turns, drained on the UI thread.</para>
/// </summary>
public sealed class AgentInbox
{
    /// <summary>A ceiling so a misbehaving producer cannot grow the mailbox without bound. Distinct
    /// ids only — the upsert means progress reports never count toward this.</summary>
    public const int MaxMessages = 32;

    private readonly object _lock = new();
    private readonly List<InboxMessage> _messages = new();

    /// <summary>Raised when the mailbox gains or updates a message, so the UI can badge the tab.</summary>
    public event Action? Changed;

    public bool HasMessages { get { lock (_lock) return _messages.Count > 0; } }

    /// <summary>Adds a message, or replaces the one already filed under the same id.</summary>
    public void Post(InboxMessage message)
    {
        lock (_lock)
        {
            var index = _messages.FindIndex(m => m.Id == message.Id);
            if (index >= 0) _messages[index] = message;
            else
            {
                // Oldest out first: a stale progress report matters less than the newest event.
                if (_messages.Count >= MaxMessages) _messages.RemoveAt(0);
                _messages.Add(message);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Reads without consuming — for a tool that answers "what has happened?" mid-turn.</summary>
    public IReadOnlyList<InboxMessage> Peek()
    {
        lock (_lock) { return _messages.ToList(); }
    }

    /// <summary>Takes everything and empties the mailbox. Called once per turn, at send time.</summary>
    public IReadOnlyList<InboxMessage> Drain()
    {
        List<InboxMessage> taken;
        lock (_lock)
        {
            taken = _messages.ToList();
            _messages.Clear();
        }
        if (taken.Count > 0) Changed?.Invoke();
        return taken;
    }

    /// <summary>Drops one message by id — used when a delegation is superseded or cancelled.</summary>
    public void Remove(string id)
    {
        bool removed;
        lock (_lock) { removed = _messages.RemoveAll(m => m.Id == id) > 0; }
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// The mailbox as background for the model. Framed as things that HAPPENED rather than as
    /// anything said to it, so a delegation report is never mistaken for the user speaking.
    /// </summary>
    public static string Format(IReadOnlyList<InboxMessage> messages)
    {
        if (messages.Count == 0) return "";
        var noun = messages.Count == 1 ? "update" : "updates";
        return $"[While you were away — {messages.Count} {noun} about work you set in motion. " +
               "Background you now have; mention it only if it is relevant.]\n" +
               string.Join("\n\n", messages.Select(m => $"— {m.Subject}\n{m.Body}"));
    }
}
