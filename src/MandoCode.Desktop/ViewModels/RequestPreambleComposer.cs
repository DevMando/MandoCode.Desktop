namespace MandoCode.Desktop.ViewModels;

/// <summary>
/// Assembles the invisible preamble that rides along with a user's message — imported snapshot
/// recaps, emoji reactions, workspace changes made outside the conversation, and a planning nudge.
/// Extracted from <c>ChatController.SubmitAsync</c> as a pure function so the exact framing (which
/// the model sees but the user never does) can be unit-tested. Each block that fires wraps the
/// running text with its own "[Current request:]" boundary, in the order armed → reactions →
/// workspace; the planning nudge is appended last. All three collections are framed as background
/// facts, never as text the user typed.
/// </summary>
public static class RequestPreambleComposer
{
    public static string Compose(
        string request,
        IReadOnlyList<string> armedContexts,
        IReadOnlyList<(string Emoji, string Snippet)> reactions,
        IReadOnlyList<string> workspaceNotes,
        bool needsPlanning)
    {
        var result = request;

        // Imported snapshots ride along ONCE as background the model already knows — the user's
        // echoed message stays their own text. Multiple imports accumulate, each a distinct recap.
        if (armedContexts.Count > 0)
        {
            var noun = armedContexts.Count == 1 ? "recap" : "recaps";
            result =
                $"[Imported context — {armedContexts.Count} {noun} from previous conversations. " +
                "Treat as background you already have; do not reply to it directly.]\n" +
                string.Join("\n\n", armedContexts) +
                "\n\n[Current request:]\n" + result;
        }

        // Emoji reactions: factual feedback the model can steer on, framed as metadata so it's
        // never mistaken for typed text.
        if (reactions.Count > 0)
        {
            var lines = string.Join("\n", reactions.Select(r =>
                $"- {r.Emoji} on your response beginning: “{r.Snippet}”"));
            result =
                "[The user reacted to earlier responses with emoji — a feedback signal, not text they typed:]\n" +
                lines +
                "\n\n[Current request:]\n" + result;
        }

        // Workspace changes the model didn't make and can't see. Framed as facts with an explicit
        // staleness warning, so the model re-reads rather than trusting its memory of file contents.
        if (workspaceNotes.Count > 0)
        {
            var notes = string.Join("\n", workspaceNotes.Select(n => "- " + n));
            result =
                "[Workspace changes since your last turn, made outside this conversation. " +
                "Your memory of affected file contents may be stale — re-read before relying on it:]\n" +
                notes +
                "\n\n[Current request:]\n" + result;
        }

        if (needsPlanning)
        {
            result += "\n\n[system: this request looks multi-step. " +
                      "Call propose_plan now with the breakdown before doing any work.]";
        }

        return result;
    }
}
