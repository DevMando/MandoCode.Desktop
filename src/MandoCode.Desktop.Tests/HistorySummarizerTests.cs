using MandoCode.Desktop.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The plain-text flattening handed to the snapshot summarizer. It must skip the system prompt at
/// index 0, label each turn by role, describe tool turns that carry no text, and produce an honest
/// placeholder when there is nothing to recap.
/// </summary>
public sealed class HistorySummarizerTests
{
    private static ChatMessageContent Sys(string t) => new(AuthorRole.System, t);
    private static ChatMessageContent Usr(string t) => new(AuthorRole.User, t);
    private static ChatMessageContent Asst(string t) => new(AuthorRole.Assistant, t);

    [Fact]
    public void HasContent_False_WhenOnlySystemPrompt()
        => Assert.False(HistorySummarizer.HasContent(new List<ChatMessageContent> { Sys("you are helpful") }));

    [Fact]
    public void HasContent_True_WhenUserSpoke()
        => Assert.True(HistorySummarizer.HasContent(new List<ChatMessageContent> { Sys("sys"), Usr("hello") }));

    [Fact]
    public void Full_SkipsSystemPrompt_AndKeepsBothTurns()
    {
        var history = new List<ChatMessageContent> { Sys("SECRET SYSTEM"), Usr("hi there"), Asst("hey back") };

        var text = HistorySummarizer.Full(history);

        Assert.DoesNotContain("SECRET SYSTEM", text);
        Assert.Contains("hi there", text);
        Assert.Contains("hey back", text);
    }

    [Fact]
    public void Full_ReturnsPlaceholder_WhenNothingToSummarize()
        => Assert.Equal("(no prior activity captured)",
            HistorySummarizer.Full(new List<ChatMessageContent> { Sys("sys") }));

    [Fact]
    public void Full_DescribesFunctionCall_WhenTextIsEmpty()
    {
        var toolTurn = new ChatMessageContent(AuthorRole.Assistant, content: null)
        {
            Items = { new FunctionCallContent("read_file", arguments: new KernelArguments { ["path"] = "Program.cs" }) }
        };
        var history = new List<ChatMessageContent> { Sys("sys"), toolTurn };

        var text = HistorySummarizer.Full(history);

        Assert.Contains("read_file", text);
        Assert.Contains("path=Program.cs", text);
    }
}
