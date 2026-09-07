using System.Text.Json;
using MandoCode.Desktop.Services;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class DesktopPreviewImageAndServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MandoPreviewImage-" + Guid.NewGuid().ToString("N"));
    public DesktopPreviewImageAndServerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private DesktopPreviewTools Tools() => new(new ProjectRootAccessor(_root));
    private static bool Ok(string json) => JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean();

    private static readonly string Png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

    [Fact]
    public async Task TextOnlyModelsRefuseBeforeCapturing()
    {
        var tools = Tools();
        var captures = 0;
        tools.ExecuteAsync = (_, _) => { captures++; return Task.FromResult("{\"ok\":true}"); };
        tools.ImageSink = new Sink { Unavailable = "This model is text-only, so a screenshot cannot be examined." };
        var result = await tools.ScreenshotDesktopPreview();
        Assert.False(Ok(result));
        Assert.Contains("text-only", result);
        Assert.Equal(0, captures);   // never pay for a capture the model cannot look at

        tools.ImageSink = null;
        Assert.False(Ok(await tools.ScreenshotDesktopPreview()));
        Assert.Equal(0, captures);
    }

    [Fact]
    public async Task CapturedImageGoesToTheModelAndNotIntoItsTextContext()
    {
        var tools = Tools();
        var sink = new Sink();
        tools.ImageSink = sink;
        tools.ExecuteAsync = (request, _) =>
        {
            Assert.Equal("screenshot", request.Operation);
            return Task.FromResult($"{{\"ok\":true,\"readyState\":\"complete\",\"image\":\"{Png}\"}}");
        };

        var result = await tools.ScreenshotDesktopPreview(note: "check the header overlap");
        Assert.True(Ok(result));
        Assert.DoesNotContain(Png, result);            // base64 must never reach the model as text
        Assert.Contains("\"imageAttached\":true", result);
        Assert.Contains("\"readyState\":\"complete\"", result);   // still counts as fresh browser evidence
        Assert.Equal(7, sink.Attached);
        Assert.Equal("image/png", sink.MediaType);
        Assert.Contains("check the header overlap", sink.Caption);
    }

    [Fact]
    public async Task RefusedDeliveryIsReportedRatherThanClaimed()
    {
        var tools = Tools();
        tools.ImageSink = new Sink { AttachError = "The image is 5000 KB, over the 4096 KB limit." };
        tools.ExecuteAsync = (_, _) => Task.FromResult($"{{\"ok\":true,\"image\":\"{Png}\"}}");
        var result = await tools.ScreenshotDesktopPreview();
        Assert.False(Ok(result));
        Assert.Contains("over the", result);
    }

    [Fact]
    public async Task NearBlankCapturesAreFlaggedRatherThanDescribed()
    {
        var tools = Tools();
        tools.ImageSink = new Sink();
        // 3160 bytes over a 900x700 viewport is what an unpainted preview actually returns.
        tools.ExecuteAsync = (_, _) => Task.FromResult(
            "{\"ok\":true,\"readyState\":\"complete\",\"viewport\":{\"width\":900,\"height\":700},\"image\":\"" +
            Convert.ToBase64String(new byte[3160]) + "\"}");
        var blank = await tools.ScreenshotDesktopPreview();
        Assert.True(Ok(blank));                       // a blank page is an observation, not an error
        Assert.Contains("\"possiblyBlank\":true", blank);
        Assert.Contains("appears blank", blank);

        tools.ExecuteAsync = (_, _) => Task.FromResult(
            "{\"ok\":true,\"readyState\":\"complete\",\"viewport\":{\"width\":900,\"height\":700},\"image\":\"" +
            Convert.ToBase64String(new byte[32000]) + "\"}");
        Assert.DoesNotContain("possiblyBlank", await tools.ScreenshotDesktopPreview());
    }

    [Theory]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://127.0.0.1:3000/about")]
    [InlineData("https://localhost:7043/")]
    public void LoopbackDevelopmentServersAreAllowed(string url) =>
        Assert.True(DesktopPreviewTools.TryResolveLocalServerUrl(url, out _, out _));

    [Theory]
    [InlineData("http://example.com:80/")]          // not loopback
    [InlineData("http://192.168.1.10:3000/")]       // the LAN is not loopback
    [InlineData("file:///C:/secrets.html")]         // not a dev server scheme
    [InlineData("http://localhost/")]               // no explicit port
    [InlineData("http://user:pw@localhost:3000/")]  // embedded credentials
    [InlineData("not a url")]
    [InlineData("")]
    public void EverythingElseIsRefused(string url) =>
        Assert.False(DesktopPreviewTools.TryResolveLocalServerUrl(url, out _, out _));

    [Fact]
    public async Task LocalServerUrlReachesTheHostAsAnOpenRequest()
    {
        var tools = Tools();
        DesktopPreviewRequest? seen = null;
        tools.ExecuteAsync = (request, _) => { seen = request; return Task.FromResult("{\"ok\":true}"); };
        Assert.True(Ok(await tools.OpenLocalServerDesktopPreview("http://localhost:5173/app")));
        Assert.Equal("open", seen!.Operation);
        Assert.Equal("http://localhost:5173/app", seen.Url);
        Assert.Null(seen.FullPath);
    }

    private sealed class Sink : IAgentImageSink
    {
        public string? Unavailable { get; set; }
        public string? AttachError { get; set; }
        public int Attached { get; private set; }
        public string MediaType { get; private set; } = "";
        public string Caption { get; private set; } = "";

        public bool TryAttach(ReadOnlyMemory<byte> bytes, string mediaType, string caption, out string error)
        {
            if (AttachError != null) { error = AttachError; return false; }
            Attached = bytes.Length;
            MediaType = mediaType;
            Caption = caption;
            error = "";
            return true;
        }
    }
}

/// <summary>
/// The seam between this app and the pinned harness submodule: a pin bump that changed either
/// side would otherwise break image delivery silently.
/// </summary>
public sealed class AiServiceVisionSeamTests
{
    [Fact]
    public void UnwiredImplementationsRefuseImagesAndReportUnknownVision()
    {
        IAiService bare = new StubAi(ModelVisionSupport.Unknown);
        Assert.Equal(ModelVisionSupport.Unknown, bare.VisionSupport);
        Assert.False(bare.TryAttachImage(new byte[] { 1 }, "image/png", "shot", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void SinkTranslatesEveryVisionStateIntoAnHonestAnswer()
    {
        Assert.Null(new AgentImageSink(new StubAi(ModelVisionSupport.Supported)).Unavailable);
        Assert.Contains("text-only", new AgentImageSink(new StubAi(ModelVisionSupport.Unsupported)).Unavailable);
        Assert.Contains("unknown", new AgentImageSink(new StubAi(ModelVisionSupport.Unknown)).Unavailable);
    }

    private class StubAi(ModelVisionSupport support) : IAiService
    {
        public ModelVisionSupport VisionSupport => support;
        public event Action<FunctionCall>? OnFunctionInvoked { add { } remove { } }
        public event Action<FunctionExecutionResult>? OnFunctionCompleted { add { } remove { } }
        public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested { get; set; }
        public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested { get; set; }
        public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested { get; set; }
        public IAsyncEnumerable<string> ChatStreamAsync(string userMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> ChatStreamWithHostInstructionAsync(string userMessage, string hostInstruction, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
}
