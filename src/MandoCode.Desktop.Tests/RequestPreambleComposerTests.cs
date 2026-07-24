using MandoCode.Desktop.ViewModels;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The invisible preamble folded into a user's message. It must leave a plain request untouched,
/// frame each ride-along as background (never as typed text), and nest them in a stable order so
/// the model always sees the actual request last.
/// </summary>
public sealed class RequestPreambleComposerTests
{
    private static readonly string[] None = System.Array.Empty<string>();
    private static readonly (string, string)[] NoReactions = System.Array.Empty<(string, string)>();

    [Fact]
    public void NoRideAlongs_ReturnsRequestUnchanged()
        => Assert.Equal("do the thing",
            RequestPreambleComposer.Compose("do the thing", None, NoReactions, None, needsPlanning: false));

    [Fact]
    public void Planning_AppendsProposePlanNudge()
    {
        var result = RequestPreambleComposer.Compose("build a feature", None, NoReactions, None, needsPlanning: true);

        Assert.StartsWith("build a feature", result);
        Assert.Contains("propose_plan", result);
    }

    [Fact]
    public void ArmedContext_IsFramedAsBackground_AndRequestComesLast()
    {
        var result = RequestPreambleComposer.Compose(
            "current ask", new[] { "earlier recap" }, NoReactions, None, needsPlanning: false);

        Assert.Contains("Imported context — 1 recap", result);
        Assert.Contains("earlier recap", result);
        // The user's actual request must sit after the last "[Current request:]" boundary.
        Assert.EndsWith("[Current request:]\ncurrent ask", result);
    }

    [Fact]
    public void MultipleArmedContexts_Pluralize()
    {
        var result = RequestPreambleComposer.Compose(
            "x", new[] { "a", "b" }, NoReactions, None, needsPlanning: false);

        Assert.Contains("2 recaps", result);
    }

    [Fact]
    public void Reactions_AreFramedAsFeedbackNotText()
    {
        var result = RequestPreambleComposer.Compose(
            "next", None, new[] { ("👍", "the part about caching") }, None, needsPlanning: false);

        Assert.Contains("reacted to earlier responses", result);
        Assert.Contains("👍", result);
        Assert.Contains("the part about caching", result);
    }

    [Fact]
    public void WorkspaceNotes_CarryStalenessWarning()
    {
        var result = RequestPreambleComposer.Compose(
            "keep going", None, NoReactions, new[] { "user ran: git checkout main" }, needsPlanning: false);

        Assert.Contains("Workspace changes since your last turn", result);
        Assert.Contains("may be stale", result);
        Assert.Contains("git checkout main", result);
    }

    [Fact]
    public void AllRideAlongs_NestWithRequestStillLast()
    {
        var result = RequestPreambleComposer.Compose(
            "the real ask",
            new[] { "recap" },
            new[] { ("🎉", "snippet") },
            new[] { "external edit" },
            needsPlanning: true);

        Assert.Contains("Imported context", result);
        Assert.Contains("reacted to earlier responses", result);
        Assert.Contains("Workspace changes since your last turn", result);
        Assert.Contains("propose_plan", result);
        // The real ask survives, followed only by the planning nudge.
        var askIndex = result.LastIndexOf("the real ask", System.StringComparison.Ordinal);
        Assert.True(askIndex >= 0);
        Assert.True(result.IndexOf("propose_plan", System.StringComparison.Ordinal) > askIndex);
    }
}
