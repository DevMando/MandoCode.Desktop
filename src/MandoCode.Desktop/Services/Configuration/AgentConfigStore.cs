using System.Text.Json;
using System.Text.Json.Nodes;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Per-agent settings that survive the process: one JSON file per agent, named by its durable
/// <see cref="AgentSession.PersistKey"/>, so a relaunch or a reopen from History brings an agent
/// back on its OWN settings rather than on whatever the global defaults happen to be now.
///
/// An agent only gets a file once its settings are actually CHANGED. Until then it has no file
/// and is seeded fresh from <see cref="ConfigCoordinator.Defaults"/> every time it loads — so a
/// global default you raise today still reaches every agent you never configured. The first
/// change snapshots the whole config and the agent is independent from then on; "Match global
/// defaults" deletes the file and puts it back to inheriting.
///
/// SECRETS ARE NEVER WRITTEN HERE. <see cref="ExcludedJsonKeys"/> is stripped from the JSON on the
/// way out, so an API key lives in exactly one file on disk (the shared ~/.mandocode/config.json)
/// no matter how many agents get configured. <see cref="ConfigCoordinator.ApplySecretsTo"/> puts
/// it back on the in-memory clone at load, so everything downstream still reads
/// <c>Config.TavilyApiKey</c> and sees a key.
///
/// Best-effort on both ends, like <see cref="WorkspaceState"/> and <see cref="PanelState"/>: an
/// unreadable or corrupt file just means that agent starts on the defaults, never a crash.
/// </summary>
public static class AgentConfigStore
{
    /// <summary>
    /// Config keys (by JSON property name) that never belong in a per-agent file. Add to this
    /// rather than special-casing a new one elsewhere.
    ///   • tavilyApiKey — a secret. One copy on disk, in the shared config, however many agents
    ///     get configured.
    ///   • mcpServers   — app-wide by design (one shared McpClientManager owns the processes), and
    ///     CreateCloneFor re-sources it from Defaults on every load, so a persisted copy would be
    ///     dead weight. Excluding it ALSO keeps the fingerprint honest: editing the MCP page
    ///     replaces this dictionary on every live agent, and a fresh dictionary can serialize in a
    ///     different order — which would read as "the user changed this agent's settings" and
    ///     silently drop every open agent out of inheriting the defaults.
    /// (EnableMcp is a separate top-level key and stays: whether an agent USES the shared servers
    /// is genuinely per-agent.)
    /// </summary>
    private static readonly string[] ExcludedJsonKeys = ["tavilyApiKey", "mcpServers"];

    private static readonly object Gate = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "agent-configs");

    private static string PathFor(string key) => Path.Combine(Folder, key + ".json");

    /// <summary>True once this agent has been configured — i.e. it no longer tracks the defaults.</summary>
    public static bool Exists(string key)
    {
        try { return File.Exists(PathFor(key)); }
        catch { return false; }
    }

    /// <summary>This agent's saved settings, or null if it has never been configured (or the file
    /// is unreadable). The returned config has NO secrets — the caller injects them.</summary>
    public static MandoCodeConfig? TryLoad(string key)
    {
        try
        {
            string json;
            lock (Gate)
            {
                if (!File.Exists(PathFor(key))) return null;
                json = File.ReadAllText(PathFor(key));
            }
            return ConfigCloning.Deserialize(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Snapshots an agent's settings, minus secrets. Returns the exact text written, so
    /// the caller can hold it as the "last persisted" fingerprint and skip no-op rewrites.</summary>
    public static string? Save(string key, MandoCodeConfig config)
    {
        try
        {
            var json = Fingerprint(config);
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                // Write-then-rename, same as SessionHistoryStore: a crash mid-write must not leave
                // a torn file where a good snapshot of the user's settings used to be.
                var tmp = PathFor(key) + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, PathFor(key), overwrite: true);
            }
            return json;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The exact bytes <see cref="Save"/> would write — secrets stripped. Comparing this
    /// against the last-saved text is how an agent detects its FIRST real change without having to
    /// intercept every one of the dozen places a config can be mutated.</summary>
    public static string Fingerprint(MandoCodeConfig config)
    {
        var node = JsonNode.Parse(ConfigCloning.Serialize(config))!.AsObject();
        foreach (var excluded in ExcludedJsonKeys) node.Remove(excluded);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Puts an agent back to inheriting the global defaults.</summary>
    public static void Delete(string key)
    {
        try { lock (Gate) File.Delete(PathFor(key)); }
        catch { }
    }

    /// <summary>Drops configs for agents that are neither open nor recoverable from History —
    /// tabs lost to a crash, folders pruned. Same keep-set as the transcript/memory sweeps.</summary>
    public static void Sweep(IEnumerable<string> liveKeys)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var keep = new HashSet<string>(liveKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }
}
