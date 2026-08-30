using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Desktop.Services;

namespace MandoCode.Desktop.ViewModels;

/// <summary>
/// Drives one streamed model response: pumps <see cref="IAiService.ChatStreamAsync"/>, flushes each
/// completed turn to the transcript as its own card, and handles the empty-response, 401, and
/// cancellation cases plus the token summary. Lifted out of <c>ChatController</c> so it can be tested
/// with a fake <see cref="IAiService"/> — it depends only on constructible, WinUI-free collaborators
/// (the request-lifecycle bits — the CancellationTokenSource, StateChanged, operation-field resets —
/// stay in ChatController). The 401 sign-in walkthrough is a UI wizard, so it arrives as the
/// <see cref="On401"/> callback rather than being called directly.
/// </summary>
public sealed class ResponseStreamer
{
    private readonly IAiService _ai;
    private readonly TranscriptWriter _transcript;
    private readonly ITranscriptHtml _html;
    private readonly BusyStateService _busy;
    private readonly TokenTrackingService _tokenTracker;
    private readonly MandoCodeConfig _config;

    public ResponseStreamer(
        IAiService ai,
        TranscriptWriter transcript,
        ITranscriptHtml html,
        BusyStateService busy,
        TokenTrackingService tokenTracker,
        MandoCodeConfig config)
    {
        _ai = ai;
        _transcript = transcript;
        _html = html;
        _busy = busy;
        _tokenTracker = tokenTracker;
        _config = config;
    }

    /// <summary>Logs conversational turns ("a" for each assistant turn). Set by ChatController so the
    /// same logger records both user and assistant turns.</summary>
    public Action<string, string>? ConversationLogger { get; set; }

    /// <summary>Invoked when a response looks like a cloud 401 — ChatController wires the sign-in
    /// walkthrough here. Null in tests.</summary>
    public Func<Task>? On401 { get; set; }

    /// <summary>Streams a request to completion and writes it to the transcript. Returns the joined
    /// response text (empty when the model returned nothing, was cancelled, or errored) so the caller
    /// can remember the last response. Never throws — cancellation and errors surface as transcript
    /// lines, matching the original in-controller behavior.</summary>
    public async Task<string> StreamAsync(string input, CancellationToken token)
    {
        try
        {
            var enumerator = _ai.ChatStreamAsync(input, token).GetAsyncEnumerator(token);
            try
            {
                _busy.Start("Thinking...");

                if (!await enumerator.MoveNextAsync())
                {
                    _busy.Stop();
                    _transcript.Append(_html.Warn("No response from model. The request may have exceeded the model's context window."));
                    _transcript.Append(_html.Dim("Try a smaller request, or switch to a model with a larger context window via /config set."));
                    return "";
                }

                // Each element is one completed chat turn — auto-continuations and post-approval turns
                // arrive as additional elements. Flush every turn as it completes so its text lands
                // next to the approval/diff cards it belongs with, instead of coalescing into one card.
                var segments = new List<string>();
                do
                {
                    var segment = enumerator.Current.Trim();
                    if (segment.Length > 0)
                    {
                        segments.Add(segment);
                        _transcript.Append(_html.AssistantCard(segment, _config.AgentName));
                        ConversationLogger?.Invoke("a", segment);
                    }
                } while (await enumerator.MoveNextAsync());

                _busy.Stop();

                if (segments.Count == 0)
                {
                    _transcript.Append(_html.Warn("Model returned an empty response. The context may be too large for this model."));
                    _transcript.Append(_html.Dim("Try a smaller request, or switch to a model with a larger context window."));
                    return "";
                }

                var responseText = string.Join("\n\n", segments);

                if (Looks401(responseText) && On401 != null)
                {
                    // 401 auto-recovery — same intent as the CLI's TryAutoSigninAfter401Async:
                    // offer the sign-in walkthrough inline instead of making the user type /setup.
                    await On401();
                }
                else if (Looks403(responseText))
                {
                    // 403 is NOT a sign-in problem — the account is authenticated but has no
                    // cloud subscription, so the sign-in walkthrough would loop uselessly.
                    // Name the real cause and the two real exits.
                    _transcript.Append(_html.Warn("403 Forbidden from the cloud — your ollama.com account is signed in but doesn't have an active cloud subscription."));
                    _transcript.Append(_html.Dim("Cloud models require a subscription at ollama.com. Or switch to a free local model with /model."));
                }

                if (_config.EnableTokenTracking)
                {
                    var lastOp = _tokenTracker.LastOperation;
                    if (lastOp != null)
                    {
                        var tps = lastOp.TokensPerSecond.HasValue ? $": {lastOp.TokensPerSecond.Value:0.#} tok/s" : "";
                        _transcript.Append(_html.TokenSummary(
                            $"[~{TokenTrackingService.FormatTokenCount(lastOp.PromptTokens)} in, " +
                            $"{TokenTrackingService.FormatTokenCount(lastOp.CompletionTokens)} out{tps}]"));
                    }
                }

                return responseText;
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            _transcript.Append(_html.Warn("Request cancelled."));
            return "";
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Error($"Error: {ex.Message}"));
            return "";
        }
    }

    private static bool Looks401(string responseText)
        => !string.IsNullOrEmpty(responseText)
           && responseText.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase);

    private static bool Looks403(string responseText)
        => !string.IsNullOrEmpty(responseText)
           && responseText.Contains("403", StringComparison.Ordinal)
           && responseText.Contains("forbidden", StringComparison.OrdinalIgnoreCase);
}
