using System.Text.Json;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Pure <see cref="MandoCodeConfig"/> cloning — a JSON round-trip followed by a mandatory
/// <see cref="MandoCodeConfig.ValidateAndClamp"/>. Separated from <see cref="ConfigCoordinator"/>
/// (which is coupled to live <c>AgentSession</c>s) so the round-trip can be unit-tested on its own:
/// it is the exact case-sensitivity trap the config guardrails warn about.
/// </summary>
public static class ConfigCloning
{
    // Mirrors the harness's internal ConfigJsonOptions (MandoCodeConfig.cs) — it isn't visible
    // across the assembly boundary, and the round-trip has to be symmetric with Load()/Save().
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>A fresh, fully-detached copy of <paramref name="source"/>.</summary>
    public static MandoCodeConfig DeepClone(MandoCodeConfig source)
    {
        var json = JsonSerializer.Serialize(source, WriteOptions);
        var clone = JsonSerializer.Deserialize<MandoCodeConfig>(json, ReadOptions)
            ?? throw new InvalidOperationException("Failed to clone MandoCodeConfig.");

        // Mandatory, not cosmetic. System.Text.Json builds McpServers with the default
        // case-SENSITIVE comparer regardless of the property initializer; ValidateAndClamp
        // rebuilds it as OrdinalIgnoreCase. Skip this and every MCP server lookup in the clone
        // silently misses on a casing difference.
        clone.ValidateAndClamp();
        return clone;
    }
}
