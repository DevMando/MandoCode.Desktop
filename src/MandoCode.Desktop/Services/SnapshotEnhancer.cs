using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace MandoCode.Desktop.Services;

/// <summary>
/// One-off LLM summarizer that turns a buffered conversation into a snapshot's recap. Builds a bare
/// Ollama kernel with no plugins, tools, filters, or shared history, so summarizing can never touch a
/// live agent's conversation or trigger a tool call. Fed the full untruncated history from
/// <c>ChatController.PendingSnapshot</c>; the result becomes <see cref="ContextSnapshot.Recap"/>.
///
/// Summarizes the ENTIRE conversation via map-reduce: the history is chunked, each chunk is
/// summarized, then the partial summaries are reduced into one final recap. This mirrors the shape
/// of Semantic Kernel's <c>ConversationSummaryPlugin</c> (chunk → summarize → combine) but is
/// hand-rolled here so it needs no extra alpha package (Plugins.Core / TextChunker), and uses a
/// prompt tuned for coding transcripts rather than SK's generic one. If we later pull in
/// Plugins.Core, this is the natural swap point.
///
/// Lives in the Desktop tree because the harness AIService exposes no one-shot completion.
/// </summary>
public static class SnapshotEnhancer
{
    // Chunk sizing. Kept modest so a lightweight local model comprehends each chunk well, while the
    // per-conversation chunk COUNT is capped so a huge history can't fan out into dozens of calls —
    // instead chunks grow coarser. Either way the whole conversation is covered, never truncated.
    private const int MinChunkChars = 6000;    // ~1.5k tokens — comfortable for small models
    private const int MaxChunks = 16;          // bounds the number of local calls on giant histories

    // A snapshot recap is Imported and silently prepended to ANOTHER model's next message, so the
    // prompts frame it as a HANDOFF BRIEFING to an AI assistant — not a human-facing summary. They're
    // domain-agnostic (coding, research Q&A, debugging, plain chat), weight the most recent turns
    // (where the current state lives), preserve specifics verbatim, and emit PLAIN PROSE (the card
    // renders the recap as plain text, so markdown/asterisks would leak).
    private const string StyleRules =
        " Write plain, dense prose addressed to the assistant (e.g. \"The user is building…\"). No " +
        "markdown, headings, bullets, asterisks, or backticks. Preserve specifics VERBATIM — file " +
        "paths, names, numbers, versions, URLs, identifiers, exact decisions. Make explicit what is " +
        "already DONE versus still UNFINISHED, so the assistant knows what to work on next. Be " +
        "self-contained: don't refer to \"the conversation above.\" Use only what's actually in the " +
        "transcript — never invent or assume. Don't describe what the conversation was NOT about; " +
        "capture what it WAS about.";

    private const string MapPrompt =
        "Summarize this SEGMENT of a longer conversation as raw material for a later handoff. Capture " +
        "the substantive content: what the user wants, key facts, answers, decisions, code, files, " +
        "values, and errors, plus anything left open. State facts only — no guessing about other " +
        "segments." + StyleRules;

    private const string ReducePrompt =
        "Below are ordered segment summaries of one conversation. Merge them into a single briefing " +
        "that will be handed to another AI assistant so it can continue this conversation seamlessly. " +
        "Cover: what the user is trying to do, the key facts / decisions / code established so far, " +
        "any preferences or constraints the user stated, and the immediate open thread or next step " +
        "(including any unanswered question). Give extra weight to the most recent exchanges — that's " +
        "where things currently stand." + StyleRules + " Keep it to a tight paragraph or two; length " +
        "should match how much actually happened.";

    private const string SinglePrompt =
        "You are writing a briefing that will be silently handed to another AI assistant so it can " +
        "continue this conversation without missing a beat. From the transcript below, write what " +
        "that assistant needs to know: what the user is trying to do, the key facts / answers / " +
        "decisions / code established so far, any preferences or constraints the user stated, and the " +
        "immediate open thread or next step (including any unanswered question). Give extra weight to " +
        "the most recent exchanges — that's where things currently stand." + StyleRules + " Keep it " +
        "to a tight paragraph or two; length should match how much actually happened.";

    /// <summary>Generates an AI recap of <paramref name="rawHistory"/> using the given Ollama model.
    /// Throws on connection/model failure; the caller decides how to surface it.</summary>
    public static async Task<string> SummarizeAsync(
        string endpoint, string model, string rawHistory, CancellationToken ct = default)
    {
        var kernel = Kernel.CreateBuilder()
            .AddOllamaChatCompletion(modelId: model, endpoint: new Uri(endpoint))
            .Build();

        var chat = kernel.GetRequiredService<IChatCompletionService>();

        var chunks = Chunk(rawHistory);

        // Short conversation — one pass, straight to a final-shaped recap.
        if (chunks.Count <= 1)
            return await SummarizeOneAsync(chat, kernel, SinglePrompt, rawHistory, ct);

        // Map: summarize each segment independently.
        var partials = new List<string>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            var part = await SummarizeOneAsync(chat, kernel, MapPrompt, chunks[i], ct);
            if (!string.IsNullOrWhiteSpace(part))
                partials.Add($"Segment {i + 1}/{chunks.Count}:\n{part}");
        }

        if (partials.Count == 0) return "";

        // Reduce: fold the segment summaries into one recap.
        return await SummarizeOneAsync(chat, kernel, ReducePrompt, string.Join("\n\n", partials), ct);
    }

    private const string NamePrompt =
        "Give this saved conversation a short title so it's recognizable in a list later. 3 to 6 " +
        "words, Title Case, naming the actual subject (a feature, file, bug, topic, or decision) — " +
        "not generic filler like \"Coding Session\" or \"Conversation Summary\". Output ONLY the " +
        "title: no quotes, no trailing punctuation, no explanation.";

    /// <summary>Suggests a short, human-recognizable title for a snapshot from its recap. Best-effort:
    /// returns null (caller falls back to the origin model as the card title) on any failure or an
    /// unusable result. <paramref name="avoid"/> is passed to the model to discourage near-duplicates;
    /// the caller still enforces true uniqueness deterministically — an LLM can't be trusted to.</summary>
    public static async Task<string?> SuggestNameAsync(
        string endpoint, string model, string recap, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(recap)) return null;

            var kernel = Kernel.CreateBuilder()
                .AddOllamaChatCompletion(modelId: model, endpoint: new Uri(endpoint))
                .Build();
            var chat = kernel.GetRequiredService<IChatCompletionService>();

            var instruction = NamePrompt;
            if (avoid.Count > 0)
                instruction += " These titles are already taken, so pick something clearly different: "
                             + string.Join("; ", avoid.Take(40)) + ".";

            // A touch of warmth so titles aren't all phrased alike, but still grounded in the recap.
            var raw = await SummarizeOneAsync(chat, kernel, instruction, recap, ct, temperature: 0.4f);
            return SnapshotNaming.Clean(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One chat round: system instruction + the text to summarize. Stateless — a fresh
    /// history each call, so nothing leaks between chunks.</summary>
    private static async Task<string> SummarizeOneAsync(
        IChatCompletionService chat, Kernel kernel, string instruction, string text, CancellationToken ct,
        float temperature = 0.2f)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(instruction);
        history.AddUserMessage(text);

        // Low temperature by default — a recap should be faithful, not creative. Naming nudges higher.
        var settings = new OllamaPromptExecutionSettings { Temperature = temperature };

        var result = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
        return result.Content?.Trim() ?? "";
    }

    /// <summary>Splits the history into chunks on line boundaries. Chunk size grows if needed so the
    /// count never exceeds <see cref="MaxChunks"/> — the entire conversation is always covered.</summary>
    private static List<string> Chunk(string history)
    {
        history = history?.Trim() ?? "";
        if (history.Length <= MinChunkChars) return new List<string> { history };

        // Grow the chunk size so a very large history still fits in MaxChunks pieces.
        int chunkSize = Math.Max(MinChunkChars, (int)Math.Ceiling((double)history.Length / MaxChunks));

        var chunks = new List<string>();
        var sb = new StringBuilder(chunkSize + 256);
        foreach (var line in history.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > chunkSize)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
