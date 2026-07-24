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
