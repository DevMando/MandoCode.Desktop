using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace MandoCode.Desktop.Services;

/// <summary>
/// The assistant behind the prompt bar on the notes surfaces. Two modes: asking about the ONE note
/// that's open, and asking about the pad as a whole.
///
/// <b>It has no tools, and that is the design.</b> Like <see cref="SnapshotEnhancer"/>, this builds a
/// bare Ollama kernel with no plugins, filters, or shared history — so it cannot read or write a
/// single file. "No agent ever touches your note" is therefore true by construction rather than by
/// policy: the only route from a reply into a note is the user pressing Insert or Replace. That's also
/// why it isn't an <c>AIService</c> agent — those exist to change your files, which is the opposite of
/// what a notepad wants, and notes are app-wide while agents belong to a project folder.
///
/// <b>The note travels in the message, not as a path.</b> Notes live in <c>~/.mandocode/notes</c>,
/// outside every project root, so no root-scoped file tool could read one anyway. Sending the live
/// buffer means the model always sees the note as it is right now — including edits not yet
/// autosaved — with nothing to fall out of sync. Only the CURRENT message carries the note text;
/// earlier turns in the thread keep just their words, so a long back-and-forth doesn't ship five
/// stale copies of the same note.
/// </summary>
public static class NoteAssistant
{
    /// <summary>One exchange in a note's (in-memory, non-persisted) thread.</summary>
    public sealed record Turn(bool FromUser, string Text);

    /// <summary>How many notes' titles the pad-wide prompt describes, and how many get their full body.
    /// Both are reported to the user by the caller — a silent cap reads as "it looked at everything".</summary>
    public const int MaxIndexedNotes = 200;
    public const int MaxFullNotes = 12;
    private const int MaxFullChars = 60_000;

    /// <summary>Turns of history sent back. A note isn't a conversation; this is just enough for
    /// "shorter" or "now do the same for the second one" to mean something.</summary>
    private const int ThreadTurns = 8;

    private const string PlainTextRules =
        " Write plain text that could be pasted straight into a .txt file: no markdown fences, no " +
        "headings made of #, no bold or italics, no bullet characters other than \"- \". Keep it as " +
        "short as the question allows.";

    private const string NoteSystemPrompt =
        "You are a writing assistant attached to a single plain-text note. The note's current contents " +
        "are given in the user's message and are the only source of truth — you have no file access and " +
        "cannot change the note yourself. If the user asks you to rewrite, tidy, expand, or continue " +
        "it, reply with ONLY the replacement text, no preamble and no explanation, because the user may " +
        "put your reply straight into the note. If they ask a question, just answer it. Never invent " +
        "content and present it as something the note says." + PlainTextRules;

    private const string PadSystemPrompt =
        "You are helping someone search and make sense of their own notes. You are given a list of " +
        "their notes (title, date, first line) and the full text of some of them. Answer only from what " +
        "you were given: if the answer isn't there, say so plainly and name what you'd need to see — " +
        "never guess at the contents of a note you were only shown the title of. Refer to notes by " +
        "title so the user can find them." + PlainTextRules;

    /// <summary>
    /// Streams an answer about the open note. <paramref name="selection"/>, when non-empty, focuses the
    /// request on the selected passage — which is what makes "tighten this" mean the paragraph the user
    /// highlighted rather than the whole note.
    /// </summary>
    public static Task AskAboutNoteAsync(
        string endpoint,
        string model,
        string noteTitle,
        string noteText,
        string? selection,
        IReadOnlyList<Turn> thread,
        string question,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var message = new StringBuilder();
        message.Append("Note title: ").Append(noteTitle).Append('\n');
        message.Append("--- current contents of the note ---\n");
        message.Append(string.IsNullOrEmpty(noteText) ? "(the note is empty)" : noteText);
        message.Append("\n--- end of note ---\n");

        if (!string.IsNullOrWhiteSpace(selection))
        {
            message.Append("The user has this passage selected; act on it rather than the whole note:\n");
            message.Append("--- selection ---\n").Append(selection).Append("\n--- end of selection ---\n");
        }

        message.Append('\n').Append(question.Trim());

        return StreamAsync(endpoint, model, NoteSystemPrompt, thread, message.ToString(), onDelta, ct);
    }

    /// <summary>
    /// Streams an answer about the whole pad. <paramref name="indexed"/> supplies titles and first
    /// lines; <paramref name="full"/> is the subset whose bodies are included (the caller picks these —
    /// typically whatever the search box is currently matching).
    /// </summary>
    public static Task AskAboutPadAsync(
        string endpoint,
        string model,
        IReadOnlyList<NoteEntry> indexed,
        IReadOnlyList<NoteEntry> full,
        IReadOnlyList<Turn> thread,
        string question,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var message = new StringBuilder();

        message.Append("--- the user's notes (").Append(indexed.Count).Append(" listed) ---\n");
        foreach (var note in indexed.Take(MaxIndexedNotes))
        {
            message.Append("- \"").Append(note.Title).Append("\" (").Append(note.GroupLabel)
                   .Append(", ").Append(note.ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd")).Append(')');
            if (!string.IsNullOrEmpty(note.Preview)) message.Append(" — ").Append(note.Preview);
            message.Append('\n');
        }

        var budget = MaxFullChars;
        var included = 0;
        var bodies = new StringBuilder();
        foreach (var note in full.Take(MaxFullNotes))
        {
            if (note.Text.Length == 0) continue;
            if (included > 0 && note.Text.Length > budget) break;

            bodies.Append("\n--- full text of \"").Append(note.Title).Append("\" ---\n")
                  .Append(note.Text).Append('\n');
            budget -= note.Text.Length;
            included++;
        }

        if (included > 0)
        {
            message.Append(bodies);
            message.Append("--- end of note contents ---\n");
        }
        else
        {
            message.Append("(No note bodies were included — only the list above.)\n");
        }

        message.Append('\n').Append(question.Trim());

        return StreamAsync(endpoint, model, PadSystemPrompt, thread, message.ToString(), onDelta, ct);
    }

    /// <summary>How many of <paramref name="full"/> would actually have their body sent — so the UI can
    /// say what was read instead of implying it saw everything.</summary>
    public static int CountBodiesSent(IReadOnlyList<NoteEntry> full)
    {
        var budget = MaxFullChars;
        var included = 0;
        foreach (var note in full.Take(MaxFullNotes))
        {
            if (note.Text.Length == 0) continue;
            if (included > 0 && note.Text.Length > budget) break;
            budget -= note.Text.Length;
            included++;
        }
        return included;
    }

    /// <summary>
    /// One streamed round on a throwaway kernel. Stateless by construction: a fresh
    /// <see cref="ChatHistory"/> per call, built from the system prompt, the recent thread, and this
    /// message. Deltas arrive on a background thread — the caller marshals.
    /// </summary>
    private static async Task StreamAsync(
        string endpoint,
        string model,
        string systemPrompt,
        IReadOnlyList<Turn> thread,
        string message,
        Action<string> onDelta,
        CancellationToken ct)
    {
        var kernel = Kernel.CreateBuilder()
            .AddOllamaChatCompletion(modelId: model, endpoint: new Uri(endpoint))
            .Build();

        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);

        foreach (var turn in thread.TakeLast(ThreadTurns))
        {
            if (turn.FromUser) history.AddUserMessage(turn.Text);
            else history.AddAssistantMessage(turn.Text);
        }

        history.AddUserMessage(message);

        // Low temperature: this rewrites the user's own words, so faithful beats inventive.
        var settings = new OllamaPromptExecutionSettings { Temperature = 0.3f };

        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
        {
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(chunk.Content)) onDelta(chunk.Content);
        }
    }
}
