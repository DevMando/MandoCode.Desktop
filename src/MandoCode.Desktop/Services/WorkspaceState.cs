using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>One open tab as remembered across launches. <paramref name="Key"/> is the
/// session's durable persist-key — it reattaches the restored tab to its transcript
/// journal (null in files written before journaling existed).</summary>
public sealed record WorkspaceTabState(string Title, string ProjectRoot, string? Model, string? Key = null);

/// <summary>The workspace's shape: which tabs were open and which was active.</summary>
public sealed record WorkspaceShape(List<WorkspaceTabState> Tabs, int ActiveIndex);

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

    public static WorkspaceShape? TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath)) return null;
            var shape = JsonSerializer.Deserialize<WorkspaceShape>(File.ReadAllText(StorePath));
            return shape is { Tabs.Count: > 0 } ? shape : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WorkspaceShape shape)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(shape));
        }
        catch { /* best-effort */ }
    }
}
