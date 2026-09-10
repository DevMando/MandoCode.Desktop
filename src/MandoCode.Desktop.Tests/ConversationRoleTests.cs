using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Who said what, after the app has been closed and reopened.
///
/// <para>The bug these cover: a question from another agent was never written to the conversation
/// log at all. The log held the ANSWER with nothing before it, so a restored session re-briefed the
/// model with a dangling reply, and read_agent_transcript showed answers with nothing that had
/// prompted them.</para>
/// </summary>
public class ConversationRoleTests
{
    [Fact]
    public void AnAgentTurnIsLabelledAsAnAgentNotAsTheUser()
    {
        // Replaying a relayed question as "User:" after a restart is exactly the confusion the
        // envelope exists to prevent — and a restart is the one place a transient host instruction
        // could never reach.
        Assert.Equal("Another agent", ConversationLog.RoleLabel(ConversationLog.AgentRole));
        Assert.Equal("User", ConversationLog.RoleLabel("u"));
        Assert.Equal("Assistant", ConversationLog.RoleLabel("a"));
    }

    [Fact]
    public void AnUnknownRoleFallsBackToAssistantRatherThanVanishing()
    {
        // Logs written by an older build carry roles this one has never seen. Rendering something
        // is better than dropping a turn out of the replay.
        Assert.Equal("Assistant", ConversationLog.RoleLabel("?"));
        Assert.Equal("Assistant", ConversationLog.RoleLabel(""));
    }

    [Fact]
    public void ARelayedQuestionSurvivesARoundTripThroughTheLog()
    {
        // The full path a restart takes: wrap, persist, reload, relabel. The envelope has to still
        // be legible at the end of it.
        var key = "roundtrip-" + Guid.NewGuid().ToString("N");
        try
        {
            var envelope = PeerMessageEnvelope.Wrap("Ninja", "have you finished the X task?");
            ConversationLog.Append(key, ConversationLog.AgentRole, envelope);
            ConversationLog.Append(key, "a", "yes, both are done");

            var turns = ConversationLog.Load(key);

            Assert.Equal(2, turns.Count);
            Assert.Equal(ConversationLog.AgentRole, turns[0].R);
            Assert.Contains("sent by the agent \"Ninja\"", turns[0].T);
            Assert.Contains("NOT from the user", turns[0].T);
            Assert.Equal("Another agent", ConversationLog.RoleLabel(turns[0].R));
        }
        finally { ConversationLog.Delete(key); }
    }

    [Fact]
    public void AnAnswerIsNoLongerLeftWithoutTheQuestionThatPromptedIt()
    {
        // The shape of the original bug, asserted directly: an assistant turn with no preceding
        // turn is what a restored session used to re-brief the model with.
        var key = "paired-" + Guid.NewGuid().ToString("N");
        try
        {
            ConversationLog.Append(key, ConversationLog.AgentRole, PeerMessageEnvelope.Wrap("Ninja", "status?"));
            ConversationLog.Append(key, "a", "still building");

            var turns = ConversationLog.Load(key);

            Assert.NotEqual("a", turns[0].R);   // something precedes the answer
        }
        finally { ConversationLog.Delete(key); }
    }
}
