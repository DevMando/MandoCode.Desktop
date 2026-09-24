using System.Text;
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
///
/// <para>While a turn streams, its text shows in a live draft (<see cref="TranscriptWriter.UpdateLive"/>,
/// throttled). When a tool call starts, <see cref="SealLiveText"/> turns the text so far into its own
/// provisional card, so words written before a tool call don't merge into the answer written after
/// it. Provisional cards are only kept if they are found, in order, in the turn's final text: a model
/// that writes its tool calls as text has them parsed out after streaming, and that raw text must
/// not stay on screen.</para>
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

    /// <summary>How often the live draft repaints. Each repaint replaces one text node, but a
    /// per-token repaint is the continuous-redraw trap, so chunks are coalesced.</summary>
    public TimeSpan LiveFlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    private readonly object _liveLock = new();
    private readonly StringBuilder _liveText = new();
    private readonly List<string> _sealedText = new();
    private readonly List<string> _sealedHtml = new();
    private long _liveGen;
    private bool _liveOpen;
    private bool _flushPending;

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
    public async Task<string> StreamAsync(string input, CancellationToken token, string? hostInstruction = null)
    {
        try
        {
            _ai.OnResponseTextDelta += OnTextDelta;
            BeginLiveTurn();
            var stream = string.IsNullOrWhiteSpace(hostInstruction)
                ? _ai.ChatStreamAsync(input, token)
                : _ai.ChatStreamWithHostInstructionAsync(input, hostInstruction, token);
            var enumerator = stream.GetAsyncEnumerator(token);
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
                    var (unshown, endLive) = SettleLiveTurn(segment);
                    if (segment.Length > 0)
                    {
                        segments.Add(segment);
                        if (unshown.Length > 0)
                            _transcript.Append(_html.AssistantCard(unshown, _config.AgentName));
                        ConversationLogger?.Invoke("a", segment);
                    }
                    // After the card, so the draft is replaced rather than blinking out first.
                    endLive();
                    // The next turn's chunks only start once MoveNextAsync resumes the harness.
                    BeginLiveTurn();
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
            DiscardLiveTurn();
            _transcript.Append(_html.Warn("Request cancelled."));
            return "";
        }
        catch (Exception ex)
        {
            DiscardLiveTurn();
            _transcript.Append(_html.Error($"Error: {ex.Message}"));
            return "";
        }
        finally
        {
            _ai.OnResponseTextDelta -= OnTextDelta;
            DiscardLiveTurn();
        }
    }

    /// <summary>A tool call is starting: the text streamed so far becomes its own provisional card,
    /// ahead of the tool pill the caller appends next. No-op when nothing is streaming.</summary>
    public void SealLiveText()
    {
        lock (_liveLock)
        {
            if (!_liveOpen) return;
            var text = _liveText.ToString().Trim();
            _liveText.Clear();
            if (text.Length == 0) return;
            var html = _html.AssistantCard(text, _config.AgentName);
            _sealedText.Add(text);
            _sealedHtml.Add(html);
            _transcript.SealLive(_liveGen, html);
        }
    }

    private void OnTextDelta(string text)
    {
        lock (_liveLock)
        {
            if (!_liveOpen) return;
            _liveText.Append(text);
            if (_flushPending) return;
            _flushPending = true;
            _ = FlushLiveLaterAsync(_liveGen);
        }
    }

    private async Task FlushLiveLaterAsync(long gen)
    {
        await Task.Delay(LiveFlushInterval).ConfigureAwait(false);
        lock (_liveLock)
        {
            if (gen != _liveGen || !_liveOpen) return;   // the turn settled while we waited
            _flushPending = false;
            var text = _liveText.ToString();
            if (text.Trim().Length > 0) _transcript.UpdateLive(gen, text);
        }
    }

    private void BeginLiveTurn()
    {
        lock (_liveLock)
        {
            _liveGen++;
            _liveOpen = true;
            _flushPending = false;
            _liveText.Clear();
            _sealedText.Clear();
            _sealedHtml.Clear();
        }
    }

    /// <summary>Closes the live turn against its authoritative text. Returns the part of it that
    /// still needs a card (all of it, unless provisional cards were kept) and the call that clears
    /// the draft, which the caller makes once that card is appended.</summary>
    private (string Unshown, Action EndLive) SettleLiveTurn(string finalText)
    {
        lock (_liveLock)
        {
            if (!_liveOpen) return (finalText, () => { });
            _liveOpen = false;
            string? rest = null;
            var keep = _sealedText.Count > 0 && ReplyText.TryRemoveInOrder(finalText, _sealedText, out rest);
            if (keep)
                foreach (var html in _sealedHtml) _transcript.Journal(html);
            var gen = _liveGen;
            return (keep ? rest!.Trim() : finalText, () => _transcript.EndLive(gen, keep));
        }
    }

    private void DiscardLiveTurn()
    {
        lock (_liveLock)
        {
            if (!_liveOpen) return;
            _liveOpen = false;
            _transcript.EndLive(_liveGen, false);
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
