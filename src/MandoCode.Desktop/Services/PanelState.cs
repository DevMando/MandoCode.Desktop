using System.Text.Json;

namespace MandoCode.Desktop.Services;

/// <summary>Per-panel UI memory: which project groups are folded shut (by project label; empty means
/// all expanded), and when the user last opened each panel — the "seen" watermark that makes the
/// rail badge an unread count ("new since you last looked") rather than a running total. A null
/// watermark means never opened, so everything currently there counts as new.
/// <paramref name="LastNotePath"/> is the note the Notes panel had open when it was last closed, so
/// reopening the panel lands back in that note rather than on the list — notes are returned to far
/// more often than snapshots are.</summary>
public sealed record PanelStateShape(
    List<string> CollapsedSnapshotGroups,
    List<string> CollapsedHistoryGroups,
    DateTimeOffset? SnapshotsSeenAt = null,
    DateTimeOffset? HistorySeenAt = null,
    List<string>? CollapsedNoteGroups = null,
    string? LastNotePath = null,
    string? NoteModel = null);

/// <summary>
/// Persists per-panel UI preference — the fold state of the Snapshots and History project groups —
/// so a group you collapse stays collapsed across launches. A window-level preference like
/// Appearance, it lives outside the shared agent config. Best-effort on both ends, same as
/// <see cref="WorkspaceState"/>: a missing/corrupt file just means everything starts expanded.
/// </summary>
public static class PanelState
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MandoCode.Desktop", "panel-state.json");

    public static PanelStateShape Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var shape = JsonSerializer.Deserialize<PanelStateShape>(File.ReadAllText(StorePath));
                if (shape != null)
                    return new PanelStateShape(
                        shape.CollapsedSnapshotGroups ?? new(),
                        shape.CollapsedHistoryGroups ?? new(),
                        shape.SnapshotsSeenAt,
                        shape.HistorySeenAt,
                        shape.CollapsedNoteGroups ?? new(),
                        shape.LastNotePath,
                        shape.NoteModel);
            }
        }
        catch { /* corrupt/unreadable — start with everything expanded */ }
        return new PanelStateShape(new(), new());
    }

    public static void Save(PanelStateShape shape)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(shape));
        }
        catch { /* best-effort */ }
    }
}
