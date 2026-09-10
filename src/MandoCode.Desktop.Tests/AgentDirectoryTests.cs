using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The register behind '@' mentions. The behaviour that matters is resolution: a mention is the user
/// naming a specific colleague, so guessing wrong is worse than not resolving at all.
/// </summary>
public class AgentDirectoryTests
{
    private static AgentEntry Agent(string key, string name, bool busy = false, int step = 0, int total = 0) =>
        new(key, name, $@"C:\src\{name}", name.ToLowerInvariant(), "qwen3:8b", busy, false, step, total);

    private static AgentDirectory With(params AgentEntry[] agents)
    {
        var d = new AgentDirectory();
        d.Replace(agents);
        return d;
    }

    [Fact]
    public void ResolvesACallsignRegardlessOfCase()
    {
        var d = With(Agent("k1", "Ninja"));
        Assert.Equal("k1", d.Resolve("ninja")?.Key);
        Assert.Equal("k1", d.Resolve("NINJA")?.Key);
    }

    [Fact]
    public void AnExactNameIsNeverShadowedByALongerOne()
    {
        // "Ninja" and "NinjaTwo" both start with "Ninja". Addressing Ninja must reach Ninja.
        var d = With(Agent("k1", "Ninja"), Agent("k2", "NinjaTwo"));
        Assert.Equal("k1", d.Resolve("Ninja")?.Key);
    }

    [Fact]
    public void AnAmbiguousPrefixResolvesToNothing()
    {
        // Two candidates and no exact match: refusing is right. Picking one would silently send a
        // question to an agent the user did not name.
        var d = With(Agent("k1", "Ninja"), Agent("k2", "Nitro"));
        Assert.Null(d.Resolve("Ni"));
    }

    [Fact]
    public void AnUnambiguousPrefixResolves()
    {
        var d = With(Agent("k1", "Ninja"), Agent("k2", "Falchion"));
        Assert.Equal("k1", d.Resolve("Nin")?.Key);
    }

    [Fact]
    public void AnUnknownNameResolvesToNothing()
    {
        Assert.Null(With(Agent("k1", "Ninja")).Resolve("Sonic"));
        Assert.Null(With(Agent("k1", "Ninja")).Resolve(""));
    }

    [Fact]
    public void ThePickerExcludesTheAgentDoingTheTyping()
    {
        // An agent mentioning itself is never what was meant, and offering it invites the confusion.
        var d = With(Agent("self", "Ninja"), Agent("other", "Falchion"));
        var matches = d.Match("", excludeKey: "self");
        Assert.Equal(new[] { "Falchion" }, matches.Select(a => a.Name));
    }

    [Fact]
    public void ThePickerRanksPrefixMatchesFirst()
    {
        // Typing "ni" means you are probably reaching for Ninja, not for the agent that merely
        // contains those letters.
        var d = With(Agent("k1", "Hornight"), Agent("k2", "Ninja"));
        var matches = d.Match("ni", excludeKey: null);
        Assert.Equal(new[] { "Ninja", "Hornight" }, matches.Select(a => a.Name));
    }

    [Fact]
    public void DescribeLeadsWithWhetherTheAgentIsActuallyWorking()
    {
        // This is the sentence that answers "have you finished yet", so busy versus idle has to be
        // unmissable rather than inferred from surrounding detail.
        var busy = AgentDirectory.Describe(Agent("k1", "Ninja", busy: true, step: 3, total: 7));
        var idle = AgentDirectory.Describe(Agent("k2", "Falchion"));

        Assert.Contains("WORKING", busy);
        Assert.Contains("step 3 of 7", busy);
        Assert.Contains("IDLE", idle);
        Assert.DoesNotContain("step", idle);
    }

    [Fact]
    public void SnapshotsDoNotChangeUnderTheCaller()
    {
        // Tools read the register on model-loop threads while the UI republishes it. A caller that
        // took a list must keep the list it took.
        var d = With(Agent("k1", "Ninja"));
        var taken = d.All;
        d.Replace(new[] { Agent("k2", "Falchion") });
        Assert.Equal(new[] { "Ninja" }, taken.Select(a => a.Name));
    }
}
