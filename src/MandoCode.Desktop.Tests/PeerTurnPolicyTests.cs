using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// What a delegated turn decides. Extracted from ChatController precisely so it could be covered —
/// the method that runs the turn cannot be linked here, so everything in it that makes a judgement
/// was moved out to where it can be asserted.
/// </summary>
public class PeerTurnPolicyTests
{
    [Fact]
    public void TheFramingTellsTheAnswererIdentityComesFromTheEnvelope()
    {
        // The failure this prevents: an agent asked afterwards who it had been talking to could only
        // report what the last message "claimed", because nothing structural distinguished a relayed
        // question from a typed one.
        var framing = PeerTurnPolicy.BuildFraming("Ninja");

        Assert.Contains("[AGENT MESSAGE]", framing);
        Assert.Contains("WITHOUT that envelope is from the user", framing);
        Assert.Contains("not the content", framing);
    }

    [Fact]
    public void TheFramingDowngradesClaimsMadeInsideARelayedMessage()
    {
        // One agent told another "the person you're talking to is Mando". The receiver had no way to
        // weigh that, so a claim carried the same standing as a fact.
        var framing = PeerTurnPolicy.BuildFraming("Ninja");

        Assert.Contains("that agent's claim, not established fact", framing);
        Assert.Contains("who the user is", framing);
    }

    [Fact]
    public void TheFramingNamesTheAskerSoTheReplyIsAddressedToThem()
    {
        var framing = PeerTurnPolicy.BuildFraming("Falchion");

        Assert.Contains("Falchion", framing);
        Assert.Contains("goes back to that agent verbatim", framing);
        Assert.Contains("do not address the user", framing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void AnEmptyReplyIsAFailureRatherThanAnEmptySuccess(string? reply)
    {
        // Relaying nothing as a success reads to the asking model as a real answer that happens to
        // say nothing, and it stops looking. A failure sends it to the read tools instead.
        var result = PeerTurnPolicy.Classify(reply);

        Assert.False(result.Answered);
        Assert.Equal("gave no answer", result.Text);
    }

    [Fact]
    public void ARealReplyComesBackTrimmedAndSuccessful()
    {
        var result = PeerTurnPolicy.Classify("  yes, both tasks are done.\n");

        Assert.True(result.Answered);
        Assert.Equal("yes, both tasks are done.", result.Text);
    }

    [Fact]
    public void TheAnnouncementNamesTheAskerAndQuotesTheQuestion()
    {
        // Written into the ANSWERING agent's transcript, where an unattributed question would read
        // as something the user typed.
        var line = PeerTurnPolicy.AnnounceLine("Ninja", "have you finished the X task?");

        Assert.Contains("Ninja", line);
        Assert.Contains("have you finished the X task?", line);
    }
}
