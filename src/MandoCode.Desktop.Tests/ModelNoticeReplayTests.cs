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
    public void TheRetiredCloudNoticeIsStrippedInBothOfItsWordings()
    {
        // The notice is no longer produced at all — a model switch says nothing about cloud
        // subscriptions now. But it sits in the journals of anyone who switched to a cloud model
        // before it was removed, and replay is the ONLY thing keeping it off screen, so both
        // shipped wordings have to stay recognised.
        Assert.True(ModelNoticeReplay.IsTransient(
            Notice("Cloud models run on ollama.com and need an active cloud subscription.")));

        // The 0.14.x wording. Journaled HTML-ENCODED, because it contains an apostrophe — matching
        // it against a raw literal silently fails, which is how it would come back on restore.
        Assert.True(ModelNoticeReplay.IsTransient(
            Notice("Cloud model — runs on ollama.com&#39;s servers and needs an account with an " +
                   "active cloud subscription. Without one, requests return 403 Forbidden.")));
    }

    [Fact]
    public void QuotedWordingAndActualWarningsArePreserved()
    {
        Assert.False(ModelNoticeReplay.IsTransient($"<div class=\"assistant\">{Notice(Sizing)}</div>"));
        Assert.False(ModelNoticeReplay.IsTransient($"<div class=\"user-echo\">{Sizing}</div>"));
        Assert.False(ModelNoticeReplay.IsTransient(Notice(Sizing, "warn")));
        Assert.False(ModelNoticeReplay.IsTransient(Notice("The context window is too small for this request.")));
        Assert.False(ModelNoticeReplay.IsTransient(Notice("Project root changed to: C:\\project")));

        // The setup wizard's cloud-vs-local explainer is also a dim notice and mentions the same
        // subscription. It is part of a walkthrough the user went through, so it is history and
        // must survive — the match is on the exact retired notices, not on the word "cloud".
        Assert.False(ModelNoticeReplay.IsTransient(Notice(
            "Cloud models run on ollama.com&#39;s servers: more capable, no GPU needed, but they " +
            "require an ollama.com account with an active cloud subscription. Local models run " +
            "privately on your own hardware, free — bigger is smarter but needs more memory.")));
    }
}
