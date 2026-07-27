using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class AgentCallsignsTests
{
    [Fact]
    public void Pool_HasAtLeast500Names()
        => Assert.True(AgentCallsigns.Pool.Count >= 500,
            $"Pool has {AgentCallsigns.Pool.Count} names; the feature promises 500.");

    [Fact]
    public void Pool_NamesAreUniqueCaseInsensitive()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupes = AgentCallsigns.Pool.Where(n => !seen.Add(n)).ToList();
        Assert.True(dupes.Count == 0, "Duplicate callsigns: " + string.Join(", ", dupes));
    }

    [Fact]
    public void Pool_NamesAreWellFormed()
    {
        foreach (var name in AgentCallsigns.Pool)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name, name.Trim());
            // "Agent N" is the numbered scheme's namespace; a callsign colliding with it
            // would make AgentNaming reuse-the-lowest-free-number logic misfire.
            Assert.DoesNotContain("Agent ", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Next_DoesNotRepeatWithinOneFullCycle()
    {
        AgentCallsigns.ResetDeck();
        var dealt = new List<string>();
        for (var i = 0; i < AgentCallsigns.Pool.Count; i++)
            dealt.Add(AgentCallsigns.Next(Array.Empty<string>()));

        Assert.Equal(AgentCallsigns.Pool.Count, dealt.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Next_SkipsNamesWornByOpenTabs()
    {
        AgentCallsigns.ResetDeck();
        var taken = AgentCallsigns.Pool.Take(50).ToArray();
        for (var i = 0; i < 100; i++)
        {
            var name = AgentCallsigns.Next(taken);
            Assert.DoesNotContain(name, taken, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Next_FallsBackToNumbersWhenEveryCallsignIsTaken()
    {
        AgentCallsigns.ResetDeck();
        var everything = AgentCallsigns.Pool.ToList();
        var name = AgentCallsigns.Next(everything);
        Assert.Equal("Agent 1", name);
    }
}
