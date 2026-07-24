using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// Default agent labels reuse the lowest free "Agent N" slot, so closing a middle tab and opening a
/// new one refills the gap rather than climbing forever. User-renamed tabs are just taken names.
/// </summary>
public sealed class AgentNamingTests
{
    [Fact]
    public void FirstAgent_IsAgentOne()
        => Assert.Equal("Agent 1", AgentNaming.NextFreeName(Array.Empty<string?>()));

    [Fact]
    public void SequentialWhenAllTaken()
        => Assert.Equal("Agent 3", AgentNaming.NextFreeName(new[] { "Agent 1", "Agent 2" }));

    [Fact]
    public void ReusesLowestFreeSlot()
        => Assert.Equal("Agent 2", AgentNaming.NextFreeName(new[] { "Agent 1", "Agent 3" }));

    [Fact]
    public void RenamedTitlesAreJustTakenNames()
        => Assert.Equal("Agent 2", AgentNaming.NextFreeName(new[] { "Frontend", "Agent 1" }));

    [Fact]
    public void IgnoresNullAndEmptyTitles()
        => Assert.Equal("Agent 1", AgentNaming.NextFreeName(new string?[] { null, "" }));
}
