using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class ModelNoticeReplayTests
{
    private const string Sizing = "Context window sized to 16k tokens for this model tier (applies from your next message).";
    private static string Notice(string text, string kind = "dim") =>
        $"<div class=\"notice-card {kind}\"><span class=\"notice-emoji\">ℹ️</span><span class=\"notice-text\">{text}</span></div>";

    [Fact]
    public void RepeatedRestoresDropAllHistoricalSetupNoticesButKeepConversation()
    {
        var answer = "<div class=\"assistant\">How can I help?</div>";
        var history = new[] { answer, Notice(Sizing), Notice(Sizing), Notice(Sizing),
            Notice("Cloud models run on ollama.com and need an active cloud subscription.") };
        var replay = history.Where(h => !ModelNoticeReplay.IsTransient(h)).ToArray();
        Assert.Equal(new[] { answer }, replay);
        Assert.Equal(replay, replay.Where(h => !ModelNoticeReplay.IsTransient(h)));
    }

    [Fact]
    public void QuotedWordingAndActualWarningsArePreserved()
    {
        Assert.False(ModelNoticeReplay.IsTransient($"<div class=\"assistant\">{Notice(Sizing)}</div>"));
        Assert.False(ModelNoticeReplay.IsTransient($"<div class=\"user-echo\">{Sizing}</div>"));
        Assert.False(ModelNoticeReplay.IsTransient(Notice(Sizing, "warn")));
        Assert.False(ModelNoticeReplay.IsTransient(Notice("The context window is too small for this request.")));
        Assert.False(ModelNoticeReplay.IsTransient(Notice("Project root changed to: C:\\project")));
    }
}
