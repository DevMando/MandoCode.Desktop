using MandoCode.Desktop.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The plain-text flattening handed to the snapshot summarizer. It must skip the system prompt at
/// index 0, label each turn by role, describe tool turns that carry no text, and produce an honest
/// placeholder when there is nothing to recap.
/// </summary>
public sealed class HistorySummarizerTests
{
    private static ChatMessage Sys(string t) => new(ChatRole.System, t);
    private static ChatMessage Usr(string t) => new(ChatRole.User, t);
    private static ChatMessage Asst(string t) => new(ChatRole.Assistant, t);

    [Fact]
    public void HasContent_False_WhenOnlySystemPrompt()
        => Assert.False(HistorySummarizer.HasContent(new List<ChatMessage> { Sys("you are helpful") }));

    [Fact]
    public void HasContent_True_WhenUserSpoke()
        => Assert.True(HistorySummarizer.HasContent(new List<ChatMessage> { Sys("sys"), Usr("hello") }));

    [Fact]
    public void Full_SkipsSystemPrompt_AndKeepsBothTurns()
    {
        var history = new List<ChatMessage> { Sys("SECRET SYSTEM"), Usr("hi there"), Asst("hey back") };

        var text = HistorySummarizer.Full(history);

        Assert.DoesNotContain("SECRET SYSTEM", text);
        Assert.Contains("hi there", text);
        Assert.Contains("hey back", text);
    }

    [Fact]
    public void Full_ReturnsPlaceholder_WhenNothingToSummarize()
        => Assert.Equal("(no prior activity captured)",
            HistorySummarizer.Full(new List<ChatMessage> { Sys("sys") }));

    [Fact]
    public void Full_DescribesFunctionCall_WhenTextIsEmpty()
    {
        var toolTurn = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "Program.cs" })
        });
        var history = new List<ChatMessage> { Sys("sys"), toolTurn };

        var text = HistorySummarizer.Full(history);

        Assert.Contains("read_file", text);
        Assert.Contains("path=Program.cs", text);
    }
}
