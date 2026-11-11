using MandoCode.Desktop.Services;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class PromptImageAttachmentTests
{
    [Theory]
    [InlineData("89504E470D0A1A0A", "image/png")]
    [InlineData("FFD8FF", "image/jpeg")]
    [InlineData("474946383961", "image/gif")]
    [InlineData("474946383761", "image/gif")]
    [InlineData("524946460000000057454250", "image/webp")]
    public void UsesFileSignatureRatherThanExtension(string hex, string expected)
    {
        var image = PromptImageAttachment.Create("misleading.txt", Convert.FromHexString(hex));
        Assert.Equal(expected, image.MediaType);
    }

    [Fact]
    public void CopiesDraftBytesSoClipboardBuffersCannotChangeQueuedEvidence()
    {
        var bytes = Convert.FromHexString("89504E470D0A1A0A");
        var image = PromptImageAttachment.Create("screen.png", bytes);
        bytes[0] = 0;
        Assert.Equal(137, image.Bytes.Span[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3C7376673E")]
    [InlineData("424D00000000")]
    public void RejectsEmptyAndUnsupportedBinary(string hex) =>
        Assert.Throws<InvalidDataException>(() => PromptImageAttachment.Create("image.png", Convert.FromHexString(hex)));

    [Fact]
    public void EnforcesEngineByteLimit()
    {
        Assert.Throws<InvalidDataException>(() => PromptImageAttachment.Create("large.png", new byte[PromptImageAttachment.MaxBytes + 1]));
    }

    [Theory]
    [InlineData(ModelVisionSupport.Unknown)]
    [InlineData(ModelVisionSupport.Unsupported)]
    public void RechecksVisionWhenSubmittingAfterAModelSwitch(ModelVisionSupport vision)
    {
        Assert.NotNull(PromptImageAttachment.SubmissionError("Explain this", vision, true, false));
        Assert.Null(PromptImageAttachment.SubmissionError("Explain this", ModelVisionSupport.Supported, true, false));
    }

    [Theory]
    [InlineData(" /model")]
    [InlineData("/clear")]
    [InlineData(" !echo hello")]
    public void ImagesCannotBeQueuedByCommands(string prompt) =>
        Assert.NotNull(PromptImageAttachment.SubmissionError(prompt, ModelVisionSupport.Supported, true, false));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RefusesUnavailableConnectionOrModel(bool connected, bool error) =>
        Assert.NotNull(PromptImageAttachment.SubmissionError("Look", ModelVisionSupport.Supported, connected, error));

    [Fact]
    public void ImageOnlySubmissionHasAnExplicitPrompt()
    {
        Assert.Equal("Describe the attached image.", PromptImageAttachment.MessageText("  "));
        Assert.Equal("Compare the colors", PromptImageAttachment.MessageText("Compare the colors"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AcceptsSupportedBatchSizes(int count) =>
        Assert.Null(PromptImageAttachment.SubmissionError("Compare these", ModelVisionSupport.Supported, true, false, count));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void RefusesInvalidBatchBeforeQueueing(int count) =>
        Assert.NotNull(PromptImageAttachment.SubmissionError("Compare these", ModelVisionSupport.Supported, true, false, count));

    [Fact]
    public void MultipleImageOnlyMessageDescribesTheWholeBatch() =>
        Assert.Equal("Describe the attached images.", PromptImageAttachment.MessageText("", 4));

    [Fact]
    public async Task FourCaptionedImagesFitTheSharedEngineQueue()
    {
        var config = new MandoCodeConfig();
        var root = new MandoCode.Services.ProjectRootAccessor(Path.GetTempPath());
        var ai = new MandoCode.Services.AIService(root, config,
            new MandoCode.Services.TokenTrackingService(), new MandoCode.Services.PlanHandoff(),
            new MandoCode.Services.SkillLoader(config, root), new MandoCode.Services.McpClientManager(config),
            new MandoCode.Services.McpApprovalGate(config), new MandoCode.Services.SpinnerService());
        using var http = new HttpClient(new VisionHandler());
        // Use the engine's existing HTTP test seam without a live Ollama daemon.
        var validate = typeof(MandoCode.Services.AIService).GetMethod("ValidateModelAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, types: new[] { typeof(HttpClient) }, modifiers: null)!;
        await (Task)validate.Invoke(ai, new object[] { http })!;
        var image = PromptImageAttachment.Create("image.png", Convert.FromHexString("89504E470D0A1A0A"));
        for (var index = 0; index < PromptImageAttachment.MaxImages; index++)
            Assert.True(ai.TryAttachImage(image.Bytes, image.MediaType, $"Image {index + 1}", out var error), error);
        Assert.False(ai.TryAttachImage(image.Bytes, image.MediaType, "Extra", out _));
    }

    private sealed class VisionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"capabilities\":[\"vision\"]}") });
    }

}
