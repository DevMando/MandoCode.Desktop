using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>The write half of the round-trip, on its own. <see cref="AgentConfigStore"/>
    /// persists through this rather than through its own serializer so a saved agent config and a
    /// clone can never disagree about casing or indentation.</summary>
    public static string Serialize(MandoCodeConfig config) =>
        JsonSerializer.Serialize(config, WriteOptions);

    /// <summary>The read half. Returns null on malformed JSON — callers here are all best-effort
    /// restores that fall back to the defaults rather than failing.</summary>
    public static MandoCodeConfig? Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, ReadOptions);
        // Same reason as DeepClone below: System.Text.Json rebuilds McpServers with the default
        // case-SENSITIVE comparer, and every lookup in it would then miss on a casing difference.
        config?.ValidateAndClamp();
        return config;
    }

    // Reflected once. Using reflection rather than a hand-written field list means a config key
    // added by the CLI harness is carried across without a change here.
    private static readonly PropertyInfo[] SettableProperties = typeof(MandoCodeConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>
    /// Overwrites <paramref name="target"/>'s settings with <paramref name="source"/>'s, IN PLACE —
    /// the instance is shared with whoever captured it (an agent's AIService, SkillLoader and
    /// McpApprovalGate all hold the same object), so replacing the reference would leave every one
    /// of them on the old values.
    ///
    /// <see cref="MandoCodeConfig.AgentName"/> is never carried across. It is a tab's spoken
    /// identity rather than a setting: copying it INTO an agent (from defaults that have none)
    /// blanks that agent's name out of its own system prompt, and copying it OUT of one would stamp
    /// a single agent's name onto the defaults every other agent is seeded from.
    /// </summary>
    public static void CopyOnto(MandoCodeConfig source, MandoCodeConfig target)
    {
        // Deep-clone first: reflection assigns reference-typed members straight across, and the two
        // configs must not end up sharing List/Dictionary instances — one side's edit would be both.
        var snapshot = DeepClone(source);
        var targetAgentName = target.AgentName;

        foreach (var property in SettableProperties)
            property.SetValue(target, property.GetValue(snapshot));

        target.AgentName = targetAgentName;
        target.ValidateAndClamp();
    }

    /// <summary>
    /// The config keys (JSON property names) whose values differ between two configs. Backs the
    /// settings form's unsaved-changes count: the form edits a DRAFT clone and commits it on Save,
    /// so "what is pending" is exactly this diff against the live config.
    ///
    /// Compared as serialized JSON per property rather than by value, so lists and dictionaries
    /// (ignoreDirectories, mcpServers) compare by content instead of by reference.
    /// </summary>
    public static IReadOnlyList<string> DifferingKeys(
        MandoCodeConfig a, MandoCodeConfig b, params string[] ignore)
    {
        var left = JsonNode.Parse(Serialize(a))!.AsObject();
        var right = JsonNode.Parse(Serialize(b))!.AsObject();
        var skip = new HashSet<string>(ignore, StringComparer.Ordinal);

        var keys = new List<string>();
        foreach (var entry in left)
        {
            if (skip.Contains(entry.Key)) continue;
            if (entry.Value?.ToJsonString() != right[entry.Key]?.ToJsonString())
                keys.Add(entry.Key);
        }
        return keys;
    }

    /// <summary>A fresh, fully-detached copy of <paramref name="source"/>.</summary>
    public static MandoCodeConfig DeepClone(MandoCodeConfig source)
    {
        var json = Serialize(source);
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
