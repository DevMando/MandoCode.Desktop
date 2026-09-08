using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>ModelOrdering holds process-wide state, so every test states its own starting point.</summary>
public sealed class ModelOrderingTests
{
    private static readonly string[] Installed =
        ["zephyr:7b", "qwen2.5-coder:14b", "deepseek-v4-flash:cloud", "llama3:8b", "minimax-m3:cloud"];

    [Fact]
    public void PinnedComeFirstThenRecentThenAlphabetical()
    {
        ModelOrdering.Load(pinned: ["minimax-m3:cloud"], recent: ["llama3:8b", "zephyr:7b"]);

        Assert.Equal(
            ["minimax-m3:cloud", "llama3:8b", "zephyr:7b", "deepseek-v4-flash:cloud", "qwen2.5-coder:14b"],
            ModelOrdering.Arrange(Installed));
    }

    [Fact]
    public void APinnedModelIsNotAlsoListedUnderRecent()
    {
        ModelOrdering.Load(pinned: ["llama3:8b"], recent: ["llama3:8b", "zephyr:7b"]);

        var ordered = ModelOrdering.Arrange(Installed);

        Assert.Equal("llama3:8b", ordered[0]);
        Assert.Single(ordered, m => m == "llama3:8b");
        Assert.Equal(Installed.Length, ordered.Count);
    }

    [Fact]
    public void PinnedOrRecentModelsThatAreGoneAreSkipped()
    {
        ModelOrdering.Load(pinned: ["uninstalled:70b"], recent: ["also-gone:3b", "zephyr:7b"]);

        var ordered = ModelOrdering.Arrange(Installed);

        Assert.Equal("zephyr:7b", ordered[0]);
        Assert.Equal(Installed.Length, ordered.Count);
        Assert.DoesNotContain("uninstalled:70b", ordered);
    }

    [Fact]
    public void TheTailStaysAlphabeticalSoTheListDoesNotReshuffle()
    {
        ModelOrdering.Load(null, null);

        Assert.Equal(
            ["deepseek-v4-flash:cloud", "llama3:8b", "minimax-m3:cloud", "qwen2.5-coder:14b", "zephyr:7b"],
            ModelOrdering.Arrange(Installed));
    }

    [Fact]
    public void UseMovesAModelToTheFrontWithoutDuplicatingIt()
    {
        ModelOrdering.Load(null, recent: ["llama3:8b", "zephyr:7b"]);

        ModelOrdering.NoteUsed("zephyr:7b");

        Assert.Equal(["zephyr:7b", "llama3:8b"], ModelOrdering.Recent);
    }

    [Fact]
    public void RecentIsBoundedSoItStaysAShortlist()
    {
        ModelOrdering.Load(null, null);

        foreach (var model in new[] { "a", "b", "c", "d", "e", "f", "g" }) ModelOrdering.NoteUsed(model);

        Assert.Equal(ModelOrdering.MaxRecent, ModelOrdering.Recent.Count);
        Assert.Equal("g", ModelOrdering.Recent[0]);
        Assert.DoesNotContain("a", ModelOrdering.Recent);
    }

    [Fact]
    public void PinTogglesOffAndMatchingIgnoresCase()
    {
        ModelOrdering.Load(pinned: ["Llama3:8B"], recent: null);
        Assert.True(ModelOrdering.IsPinned("llama3:8b"));

        ModelOrdering.TogglePin("llama3:8b");
        Assert.False(ModelOrdering.IsPinned("Llama3:8B"));
        Assert.Empty(ModelOrdering.Pinned);
    }

    [Fact]
    public void BlankAndUnchangedInputAreIgnored()
    {
        ModelOrdering.Load(null, recent: ["llama3:8b"]);
        var changes = 0;
        void Count() => changes++;
        ModelOrdering.Changed += Count;
        try
        {
            ModelOrdering.NoteUsed(null);
            ModelOrdering.NoteUsed("   ");
            ModelOrdering.NoteUsed("llama3:8b");   // already on top
            ModelOrdering.TogglePin("");
        }
        finally { ModelOrdering.Changed -= Count; }

        Assert.Equal(0, changes);
        Assert.Equal(["llama3:8b"], ModelOrdering.Recent);
    }
}
