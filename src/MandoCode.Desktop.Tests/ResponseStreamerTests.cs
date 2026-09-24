using System.Runtime.CompilerServices;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The streamed-response loop, driven by a fake <see cref="IAiService"/> — the highest-risk seam in
/// the app, previously testable only by hand with a live model. Asserts each completed turn becomes
/// its own card, the empty/no-response cases warn, a 401 triggers the sign-in callback, and
/// cancellation/errors surface as transcript lines rather than throwing.
/// </summary>
public sealed class ResponseStreamerTests
{
    // Tags each fragment type so a test can assert which builder method produced a transcript block.
    private sealed class TagHtml : ITranscriptHtml
    {
        public string AssistantCard(string markdown, string? speaker = null) => $"CARD:{markdown}";
        public string Warn(string text) => $"WARN:{text}";
        public string Error(string text) => $"ERR:{text}";
        public string Dim(string text) => $"DIM:{text}";
        public string TokenSummary(string text) => $"TOK:{text}";
    }

    private sealed class FakeAiService : IAiService
    {
        private readonly string[] _segments;
        private readonly Exception? _throw;
        public string? LastHostInstruction { get; private set; }

        /// <summary>Runs before segment <c>i</c> is yielded: where a test streams chunks and starts
        /// tool calls, the way the harness does while a turn is in flight.</summary>
        public Action<int>? BeforeSegment { get; set; }

        public event Action<string>? OnResponseTextDelta;
        public void Emit(string text) => OnResponseTextDelta?.Invoke(text);

        public FakeAiService(string[] segments, Exception? throwOnStream = null)
        {
            _segments = segments;
            _throw = throwOnStream;
        }

        public async IAsyncEnumerable<string> ChatStreamAsync(
            string userMessage, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_throw != null)
            {
                await Task.Yield();
                throw _throw;
            }
            for (int i = 0; i < _segments.Length; i++)
            {
                await Task.Yield();
                BeforeSegment?.Invoke(i);
                yield return _segments[i];
            }
        }

        public IAsyncEnumerable<string> ChatStreamWithHostInstructionAsync(
            string userMessage, string hostInstruction, CancellationToken cancellationToken = default)
        {
            LastHostInstruction = hostInstruction;
            return ChatStreamAsync(userMessage, cancellationToken);
        }

        // Unused by the streaming loop.
        public event Action<FunctionCall>? OnFunctionInvoked { add { } remove { } }
        public event Action<FunctionExecutionResult>? OnFunctionCompleted { add { } remove { } }
        public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested { get; set; }
        public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested { get; set; }
        public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested { get; set; }
        public Task ReinitializeAsync(MandoCodeConfig config) => throw new NotSupportedException();
        public Task RefreshSettingsAsync(MandoCodeConfig config) => throw new NotSupportedException();
        public Task AttachMcpPluginsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool IsValid, string? ErrorMessage)> ValidateModelAsync() => throw new NotSupportedException();
        public Task<GeneratedPlan> GeneratePlanAsync(string request, string? revisionContext = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? ExportHistoryJson() => throw new NotSupportedException();
        public void AppendAssistantNote(string text) => throw new NotSupportedException();
        public void AppendUserNote(string text) => throw new NotSupportedException();
        public int TryRestoreHistoryJson(string json) => throw new NotSupportedException();
        public Task EnterLearnModeAsync() => throw new NotSupportedException();
        public Task<bool> CompactHistoryAsync() => throw new NotSupportedException();
        public Task ClearHistoryAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync() => throw new NotSupportedException();
    }

    private static (ResponseStreamer streamer, List<string> blocks) Make(FakeAiService ai)
    {
        var transcript = new TranscriptWriter();
        var blocks = new List<string>();
        transcript.BlockAdded += b => blocks.Add(b);
        var config = new MandoCodeConfig { EnableTokenTracking = false };
        var streamer = new ResponseStreamer(
            ai, transcript, new TagHtml(), new BusyStateService(), new TokenTrackingService(), config);
        return (streamer, blocks);
    }

    [Fact]
    public async Task EachTurn_BecomesItsOwnCard_AndReturnsJoinedText()
    {
        var (s, blocks) = Make(new FakeAiService(new[] { "hello", "world" }));
        var logged = new List<string>();
        s.ConversationLogger = (role, text) => logged.Add($"{role}:{text}");

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("hello\n\nworld", result);
        Assert.Contains("CARD:hello", blocks);
        Assert.Contains("CARD:world", blocks);
        Assert.Equal(new[] { "a:hello", "a:world" }, logged);
    }

    private sealed class LiveLog
    {
        public readonly List<string> Events = new();

        public LiveLog(TranscriptWriter transcript)
        {
            transcript.BlockAdded += b => Add($"block:{b}");
            transcript.BlockJournaled += b => Add($"journal:{b}");
            transcript.LiveTextChanged += (_, t) => Add($"live:{t}");
            transcript.LiveCardSealed += (_, h) => Add($"seal:{h}");
            transcript.LiveEnded += (_, keep) => Add($"end:{keep}");
        }

        private void Add(string e) { lock (Events) Events.Add(e); }
    }

    private static (ResponseStreamer streamer, LiveLog log) MakeLive(FakeAiService ai)
    {
        var transcript = new TranscriptWriter();
        var log = new LiveLog(transcript);
        var config = new MandoCodeConfig { EnableTokenTracking = false };
        var streamer = new ResponseStreamer(
            ai, transcript, new TagHtml(), new BusyStateService(), new TokenTrackingService(), config);
        return (streamer, log);
    }

    [Fact]
    public async Task TextBeforeAToolCall_BecomesItsOwnCard_AheadOfTheAnswer()
    {
        var ai = new FakeAiService(new[] { "Let me check the docs.\nHere is the answer." });
        var (s, log) = MakeLive(ai);
        ai.BeforeSegment = _ =>
        {
            ai.Emit("Let me check ");
            ai.Emit("the docs.");
            s.SealLiveText();          // what ChatController.OnFunctionInvoked does
            ai.Emit("Here is the answer.");
        };

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("Let me check the docs.\nHere is the answer.", result);
        var settled = log.Events.Where(e => !e.StartsWith("live:")).ToList();
        Assert.Equal(new[]
        {
            "seal:CARD:Let me check the docs.",
            "journal:CARD:Let me check the docs.",
            "block:CARD:Here is the answer.",
            "end:True",
            "end:False",               // the next (never-started) turn is closed on the way out
        }, settled);
    }

    [Fact]
    public async Task RewrittenFinalText_DropsProvisionalCards_AndShowsTheFinalText()
    {
        // A model that writes its tool call as text: the fallback parser strips it after streaming,
        // so the provisional card's raw text is not in the final reply and must not stay on screen.
        var ai = new FakeAiService(new[] { "The file is updated." });
        var (s, log) = MakeLive(ai);
        ai.BeforeSegment = _ =>
        {
            ai.Emit("{\"name\":\"write_file\"}");
            s.SealLiveText();
        };

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.DoesNotContain(log.Events, e => e.StartsWith("journal:"));
        Assert.Contains("block:CARD:The file is updated.", log.Events);
        Assert.Equal("end:False", log.Events.First(e => e.StartsWith("end:")));
    }

    [Fact]
    public async Task StreamedChunks_RepaintTheDraft_WithTheWholeReplySoFar()
    {
        var ai = new FakeAiService(new[] { "Hello there" });
        var (s, log) = MakeLive(ai);
        s.LiveFlushInterval = TimeSpan.FromMilliseconds(5);
        ai.BeforeSegment = _ =>
        {
            ai.Emit("Hello ");
            ai.Emit("there");
            Thread.Sleep(200);         // let the throttled repaint land before the turn settles
        };

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.Contains("live:Hello there", log.Events);
        Assert.DoesNotContain(log.Events, e => e.StartsWith("seal:"));
        Assert.Contains("block:CARD:Hello there", log.Events);
    }

    [Fact]
    public async Task SealWithoutAStreamingTurn_IsANoOp()
    {
        var ai = new FakeAiService(new[] { "done" });
        var (s, log) = MakeLive(ai);

        s.SealLiveText();
        await s.StreamAsync("hi", CancellationToken.None);
        s.SealLiveText();

        Assert.DoesNotContain(log.Events, e => e.StartsWith("seal:"));
    }

    [Fact]
    public async Task HostInstruction_UsesSeparateAiServiceChannel()
    {
        var ai = new FakeAiService(new[] { "done" });
        var (s, _) = Make(ai);

        await s.StreamAsync("the user text", CancellationToken.None, "host-owned guidance");

        Assert.Equal("host-owned guidance", ai.LastHostInstruction);
    }

    [Fact]
    public async Task NoChunks_WarnsNoResponse_AndReturnsEmpty()
    {
        var (s, blocks) = Make(new FakeAiService(Array.Empty<string>()));

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("", result);
        Assert.Contains(blocks, b => b.StartsWith("WARN:") && b.Contains("No response"));
        Assert.DoesNotContain(blocks, b => b.StartsWith("CARD:"));
    }

    [Fact]
    public async Task WhitespaceOnlyTurns_WarnEmptyResponse_NoCards()
    {
        var (s, blocks) = Make(new FakeAiService(new[] { "   ", "" }));

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("", result);
        Assert.Contains(blocks, b => b.StartsWith("WARN:") && b.Contains("empty response"));
        Assert.DoesNotContain(blocks, b => b.StartsWith("CARD:"));
    }

    [Fact]
    public async Task Response_LookingLike401_InvokesSignInCallback()
    {
        var (s, _) = Make(new FakeAiService(new[] { "Request failed: 401 Unauthorized — sign in again" }));
        var fired = false;
        s.On401 = () => { fired = true; return Task.CompletedTask; };

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.True(fired);
    }

    [Fact]
    public async Task NormalResponse_DoesNotInvoke401()
    {
        var (s, _) = Make(new FakeAiService(new[] { "all good here" }));
        var fired = false;
        s.On401 = () => { fired = true; return Task.CompletedTask; };

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.False(fired);
    }

    [Fact]
    public async Task Response_LookingLike403_ExplainsSubscription_NotSignIn()
    {
        // 403 = signed in but no cloud subscription. The sign-in walkthrough (401 recovery)
        // must NOT fire — it would loop uselessly — and the card must name the real cause.
        var (s, blocks) = Make(new FakeAiService(new[] { "Error: HTTP 403 (Forbidden) from ollama.com" }));
        var signInFired = false;
        s.On401 = () => { signInFired = true; return Task.CompletedTask; };

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.False(signInFired);
        Assert.Contains(blocks, b => b.StartsWith("WARN:") && b.Contains("subscription"));
        Assert.Contains(blocks, b => b.StartsWith("DIM:") && b.Contains("/model"));
    }

    [Fact]
    public async Task NormalResponse_DoesNotEmit403Card()
    {
        // "403" alone in prose (e.g. a line number or HTTP discussion) must not trigger —
        // the match requires "forbidden" too.
        var (s, blocks) = Make(new FakeAiService(new[] { "see RFC 9110 section 403 for details" }));

        await s.StreamAsync("hi", CancellationToken.None);

        Assert.DoesNotContain(blocks, b => b.Contains("subscription"));
    }

    [Fact]
    public async Task Cancellation_SurfacesAsWarning_NotThrow()
    {
        var (s, blocks) = Make(new FakeAiService(Array.Empty<string>(), new OperationCanceledException()));

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("", result);
        Assert.Contains("WARN:Request cancelled.", blocks);
    }

    [Fact]
    public async Task StreamError_SurfacesAsErrorCard_NotThrow()
    {
        var (s, blocks) = Make(new FakeAiService(Array.Empty<string>(), new InvalidOperationException("boom")));

        var result = await s.StreamAsync("hi", CancellationToken.None);

        Assert.Equal("", result);
        Assert.Contains(blocks, b => b.StartsWith("ERR:") && b.Contains("boom"));
    }
}
