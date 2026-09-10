using MandoCode.Desktop.Services;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// The config deep-clone every new agent starts from. The behaviour that MUST hold — warned about
/// in three places across the code yet untested until now — is that the JSON round-trip does not
/// leave <c>McpServers</c> with the case-SENSITIVE comparer System.Text.Json hands back: the clone's
/// <c>ValidateAndClamp</c> must rebuild it OrdinalIgnoreCase, or every MCP lookup in the clone
/// silently misses on a casing difference.
/// </summary>
public sealed class ConfigCloningTests
{
    [Fact]
    public void DeepClone_RebuildsMcpServers_CaseInsensitive()
    {
        var source = new MandoCodeConfig();
        source.McpServers["Solana"] = new McpServerConfig { Command = "npx" };
        // Sanity: a plain dict is case-sensitive, so the miscased lookup misses on the source.
        Assert.False(source.McpServers.ContainsKey("solana"));

        var clone = ConfigCloning.DeepClone(source);

        Assert.True(clone.McpServers.ContainsKey("solana"));
        Assert.True(clone.McpServers.ContainsKey("SOLANA"));
    }

    // ---- CopyOnto: the in-place property copy behind both "Make Default for New Agents"
    // (agent -> defaults) and "Match Global Defaults" (defaults -> agent). ----

    [Fact]
    public void CopyOnto_OverwritesTheTargetsSettings()
    {
        var source = new MandoCodeConfig { Temperature = 0.4, ModelName = "default-model" };
        var target = new MandoCodeConfig { Temperature = 0.9, ModelName = "agent-model" };

        ConfigCloning.CopyOnto(source, target);

        Assert.Equal(0.4, target.Temperature);
        Assert.Equal("default-model", target.ModelName);
    }

    [Fact]
    public void CopyOnto_NeverCarriesTheAgentName()
    {
        // AgentName is the tab's spoken identity, not a setting. Copying defaults (which have no
        // name) onto an agent must not blank that agent out of its own system prompt.
        var target = new MandoCodeConfig { AgentName = "Kernel" };

        ConfigCloning.CopyOnto(new MandoCodeConfig(), target);

        Assert.Equal("Kernel", target.AgentName);
    }

    [Fact]
    public void CopyOnto_DoesNotStampAnAgentsNameOntoTheDefaults()
    {
        // The other direction: "Make Default for New Agents" must not leave one agent's callsign
        // sitting on the config every future agent is seeded from.
        var defaults = new MandoCodeConfig();

        ConfigCloning.CopyOnto(new MandoCodeConfig { AgentName = "Kernel" }, defaults);

        Assert.Null(defaults.AgentName);
    }

    [Fact]
    public void CopyOnto_WritesThroughTheExistingInstance()
    {
        // An agent's AIService, SkillLoader and McpApprovalGate all captured this object at
        // construction. Returning a new one instead of writing through leaves them on stale values.
        var target = new MandoCodeConfig { MaxTokens = 1024 };
        var collaboratorsReference = target;

        ConfigCloning.CopyOnto(new MandoCodeConfig { MaxTokens = 8192 }, target);

        Assert.Equal(8192, collaboratorsReference.MaxTokens);
    }

    [Fact]
    public void CopyOnto_DoesNotShareCollectionsBetweenTheTwoConfigs()
    {
        // Reflection assigns reference types straight across; without the deep clone first, one
        // side's later edit would silently be both sides'.
        var source = new MandoCodeConfig();
        source.IgnoreDirectories.Add("dist");
        var target = new MandoCodeConfig();

        ConfigCloning.CopyOnto(source, target);
        target.IgnoreDirectories.Add("coverage");

        Assert.DoesNotContain("coverage", source.IgnoreDirectories);
    }

    [Fact]
    public void CopyOnto_LeavesMcpServersCaseInsensitive()
    {
        var source = new MandoCodeConfig();
        source.McpServers["Solana"] = new McpServerConfig { Command = "npx" };
        var target = new MandoCodeConfig();

        ConfigCloning.CopyOnto(source, target);

        Assert.True(target.McpServers.ContainsKey("solana"));
    }

    // ---- DifferingKeys: the unsaved-changes count behind the settings form's Save button. ----

    [Fact]
    public void DifferingKeys_IsEmptyForAFreshClone()
    {
        // A just-opened form must not claim pending changes — its draft is a clone of the live config.
        var live = new MandoCodeConfig { Temperature = 0.4, ModelName = "m" };
        live.McpServers["Solana"] = new McpServerConfig { Command = "npx" };
        live.ValidateAndClamp();

        Assert.Empty(ConfigCloning.DifferingKeys(ConfigCloning.DeepClone(live), live));
    }

    [Fact]
    public void DifferingKeys_NamesEachChangedKeyOnce()
    {
        var live = new MandoCodeConfig { Temperature = 0.4, MaxTokens = 1024 };
        var draft = ConfigCloning.DeepClone(live);
        draft.Temperature = 0.9;
        draft.MaxTokens = 2048;

        var keys = ConfigCloning.DifferingKeys(draft, live);

        Assert.Equal(2, keys.Count);
        Assert.Contains("temperature", keys);
        Assert.Contains("maxTokens", keys);
    }

    [Fact]
    public void DifferingKeys_ComparesCollectionsByContent()
    {
        // The draft holds its own List instance, so a by-reference comparison would report every
        // collection as changed and the Save button would never go quiet.
        var live = new MandoCodeConfig();
        live.IgnoreDirectories.Add("dist");
        var draft = ConfigCloning.DeepClone(live);

        Assert.DoesNotContain("ignoreDirectories", ConfigCloning.DifferingKeys(draft, live));

        draft.IgnoreDirectories.Add("coverage");
        Assert.Contains("ignoreDirectories", ConfigCloning.DifferingKeys(draft, live));
    }

    [Fact]
    public void DifferingKeys_HonoursTheIgnoreList()
    {
        // mcpServers is app-wide and edited on its own page, so the settings form excludes it —
        // otherwise an MCP edit made while the form sat open would show up as the user's pending change.
        var live = new MandoCodeConfig();
        var draft = ConfigCloning.DeepClone(live);
        draft.McpServers["github"] = new McpServerConfig { Command = "npx" };

        Assert.Contains("mcpServers", ConfigCloning.DifferingKeys(draft, live));
        Assert.Empty(ConfigCloning.DifferingKeys(draft, live, "mcpServers"));
    }

    [Fact]
    public void DifferingKeys_IsEmptyAfterCopyOnto()
    {
        // Save commits the draft with CopyOnto, then the form re-clones — which must leave it clean.
        var live = new MandoCodeConfig { Temperature = 0.4 };
        var draft = ConfigCloning.DeepClone(live);
        draft.Temperature = 0.9;

        ConfigCloning.CopyOnto(draft, live);

        Assert.Empty(ConfigCloning.DifferingKeys(draft, live));
    }

    [Fact]
    public void DeepClone_PreservesScalarValues()
    {
        var source = new MandoCodeConfig { ModelName = "qwen2.5-coder:14b", OllamaEndpoint = "http://example:1234" };

        var clone = ConfigCloning.DeepClone(source);

        Assert.Equal("qwen2.5-coder:14b", clone.ModelName);
        Assert.Equal("http://example:1234", clone.OllamaEndpoint);
    }

    [Fact]
    public void DeepClone_IsFullyDetached_MutatingCloneLeavesSourceAlone()
    {
        var source = new MandoCodeConfig();
        source.McpServers["Solana"] = new McpServerConfig { Command = "npx" };

        var clone = ConfigCloning.DeepClone(source);
        clone.McpServers.Clear();
        clone.McpServers["Other"] = new McpServerConfig { Command = "uvx" };

        Assert.True(source.McpServers.ContainsKey("Solana"));
        Assert.False(source.McpServers.ContainsKey("Other"));
    }

    [Fact]
    public void DeepClone_AppliesValidateAndClamp_HealsBlankEndpoint()
    {
        var source = new MandoCodeConfig { OllamaEndpoint = "   " };

        var clone = ConfigCloning.DeepClone(source);

        Assert.Equal("http://localhost:11434", clone.OllamaEndpoint);
    }
}
