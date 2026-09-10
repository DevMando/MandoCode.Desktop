using MandoCode.Desktop.Services;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The per-agent settings snapshot. Two claims here are load-bearing enough to be worth pinning,
/// and neither is visible from the file the user ends up with:
///
///   1. The API key is NOT in it. Per-agent settings mean one file per configured agent, so a
///      secret that rode along would be a secret in N places instead of one.
///   2. The fingerprint is STABLE against app-wide churn. An agent is "configured" — and stops
///      inheriting the defaults — precisely when its fingerprint moves, so anything that shifts the
///      fingerprint without the user changing a setting silently orphans every open agent from the
///      defaults. Replacing the shared MCP server dictionary is exactly that kind of churn.
/// </summary>
public sealed class AgentConfigStoreTests
{
    [Fact]
    public void Fingerprint_OmitsTheApiKey()
    {
        var config = new MandoCodeConfig { TavilyApiKey = "tvly-super-secret-value" };

        var json = AgentConfigStore.Fingerprint(config);

        Assert.DoesNotContain("tvly-super-secret-value", json);
        Assert.DoesNotContain("tavilyApiKey", json);
    }

    [Fact]
    public void Fingerprint_IgnoresTheApiKeyEntirely()
    {
        // Setting a key is not "configuring the agent" — it's app-wide, so it must not be what
        // pushes an agent off the defaults.
        var withoutKey = AgentConfigStore.Fingerprint(new MandoCodeConfig());
        var withKey = AgentConfigStore.Fingerprint(new MandoCodeConfig { TavilyApiKey = "tvly-abc" });

        Assert.Equal(withoutKey, withKey);
    }

    [Fact]
    public void Fingerprint_IsUnchangedWhenTheSharedMcpServersAreReplaced()
    {
        var config = new MandoCodeConfig();
        config.McpServers["solana"] = new McpServerConfig { Command = "npx" };
        config.ValidateAndClamp();
        var before = AgentConfigStore.Fingerprint(config);

        // What editing the MCP page does to every live agent (ConfigCoordinator.SyncMcpServersToAgents):
        // a whole new dictionary, with entries added in a different order.
        var replacement = new MandoCodeConfig();
        replacement.McpServers["github"] = new McpServerConfig { Command = "npx" };
        replacement.McpServers["solana"] = new McpServerConfig { Command = "uvx" };
        replacement.ValidateAndClamp();
        config.McpServers = replacement.McpServers;

        Assert.Equal(before, AgentConfigStore.Fingerprint(config));
    }

    [Fact]
    public void Fingerprint_MovesWhenARealSettingChanges()
    {
        var config = new MandoCodeConfig();
        var before = AgentConfigStore.Fingerprint(config);

        config.Temperature = config.Temperature + 0.25;

        Assert.NotEqual(before, AgentConfigStore.Fingerprint(config));
    }

    [Fact]
    public void Fingerprint_IgnoresTheAgentsOwnName()
    {
        // AgentName is [JsonIgnore] on the harness type — it names one tab, not a setting. If it
        // ever started serializing, every rename would masquerade as a settings change.
        var config = new MandoCodeConfig();
        var before = AgentConfigStore.Fingerprint(config);

        config.AgentName = "Kernel";

        Assert.Equal(before, AgentConfigStore.Fingerprint(config));
    }

    [Fact]
    public void Fingerprint_MatchesTheDefaultsAfterCopyingThemOn()
    {
        // The equality the "inheriting agent" rule rests on: after CopyOnto, an agent is
        // indistinguishable from the defaults, so AgentSession drops its saved file and goes back to
        // tracking them. If these two ever stopped agreeing, "Match Global Defaults" would leave the
        // agent permanently marked as configured.
        var defaults = new MandoCodeConfig { Temperature = 0.35, MaxTokens = 4096 };
        var agent = new MandoCodeConfig { Temperature = 0.9, MaxTokens = 512, AgentName = "Kernel" };

        ConfigCloning.CopyOnto(defaults, agent);

        Assert.Equal(AgentConfigStore.Fingerprint(defaults), AgentConfigStore.Fingerprint(agent));
    }

    [Fact]
    public void Fingerprint_MatchesTheDefaultsForAFreshClone()
    {
        // Same rule at the other end: a brand-new agent is a clone of the defaults, so it must start
        // out fingerprint-identical or every new agent would immediately look "configured".
        var defaults = new MandoCodeConfig { Temperature = 0.35 };
        defaults.McpServers["Solana"] = new McpServerConfig { Command = "npx" };
        defaults.ValidateAndClamp();

        var clone = ConfigCloning.DeepClone(defaults);

        Assert.Equal(AgentConfigStore.Fingerprint(defaults), AgentConfigStore.Fingerprint(clone));
    }

    [Fact]
    public void Fingerprint_RoundTripsThroughTheConfigReader()
    {
        // The snapshot has to be loadable by the same reader clones use, or a restored agent comes
        // back on the defaults with no sign anything went wrong.
        var config = new MandoCodeConfig
        {
            ModelName = "qwen2.5-coder:14b",
            OllamaEndpoint = "http://example:1234",
            Temperature = 0.15,
            EnableDiffApprovals = false,
        };

        var restored = ConfigCloning.Deserialize(AgentConfigStore.Fingerprint(config));

        Assert.NotNull(restored);
        Assert.Equal("qwen2.5-coder:14b", restored!.ModelName);
        Assert.Equal("http://example:1234", restored.OllamaEndpoint);
        Assert.Equal(0.15, restored.Temperature);
        Assert.False(restored.EnableDiffApprovals);
        Assert.Null(restored.TavilyApiKey);   // stripped on the way out, injected on the way in
    }
}
