using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>One open tab as remembered across launches. <paramref name="Key"/> is the
/// session's durable persist-key — it reattaches the restored tab to its transcript
/// journal (null in files written before journaling existed).</summary>
public sealed record WorkspaceTabState(string Title, string ProjectRoot, string? Model, string? Key = null);

/// <summary>The workspace's shape: which tabs were open, which was active, and the split
/// layout. <paramref name="SplitPanes"/> holds the paned agents' durable persist-keys in
/// pane order — keys, not indexes, because restore skips tabs whose project folder is gone,
/// which would shift every index. <paramref name="PaneColumnFractions"/> /
/// <paramref name="PaneRowFractions"/> are the divider positions. All three are null in files
/// written before multi-pane split view existed, which restores as "no split configured".</summary>
public sealed record WorkspaceShape(
    List<WorkspaceTabState> Tabs,
    int ActiveIndex,
    List<string>? SplitPanes = null,
    List<double>? PaneColumnFractions = null,
    List<double>? PaneRowFractions = null);

/// <summary>
/// Persists the workspace SHAPE — open tabs (title, project folder, model) and the active
/// tab — so a relaunch reopens where the user left off. Deliberately shape-only: transcript
/// and model-context persistence are separate, later tiers. Best-effort on both ends: a
/// missing/corrupt file means a default single-tab start, never a crash.
/// </summary>
public static class WorkspaceState
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "workspace.json");


    public static DesktopWorkspaceState LoadAll()
    {
        try { return Parse(File.ReadAllText(StorePath)); }
        catch { return DefaultState(); }
    }

    // Pure parser shared with migration tests. Old single-workspace files become Main.
    public static DesktopWorkspaceState Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Workspaces", out _))
        {
            var state = JsonSerializer.Deserialize<DesktopWorkspaceState>(json);
            if (state is { Version: 1, Workspaces.Count: > 0 }
                && state.Workspaces.All(w => !string.IsNullOrWhiteSpace(w.Id)
                    && !string.IsNullOrWhiteSpace(w.Name) && w.Shape?.Tabs != null)
                && state.Workspaces.Select(w => w.Id).Distinct().Count() == state.Workspaces.Count)
                return state;
            throw new InvalidDataException("Invalid workspace state.");
        }
        var legacy = JsonSerializer.Deserialize<WorkspaceShape>(json);
        if (legacy?.Tabs == null) throw new InvalidDataException("Invalid legacy workspace.");
        return new(1, "main", new() { new("main", "Main", legacy) });
    }

    private static DesktopWorkspaceState DefaultState() => new(1, "main",
        new() { new("main", "Main", new(new(), 0)) });

    public static void SaveAll(DesktopWorkspaceState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temporary = StorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, StorePath, overwrite: true);
        }
        catch { /* best-effort; atomic replacement keeps the previous valid state */ }
    }
}

public sealed record NamedWorkspaceState(string Id, string Name, WorkspaceShape Shape);
public sealed record DesktopWorkspaceState(int Version, string ActiveWorkspaceId, List<NamedWorkspaceState> Workspaces);
