using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

/// <summary>Row model for the slash-command suggestions list.</summary>
public sealed class CommandSuggestion
{
    public string Command { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>What accepting the row inserts, when that differs from <see cref="Command"/>
    /// (e.g. the ":fire:" row inserts 🔥). Null means insert the command itself.</summary>
    public string? InsertText { get; init; }
}

/// <summary>Row model for the snapshot summarizer dropdown — a model name plus whether it's a cloud
/// model (which may spend tokens) or a local one (free).</summary>
public sealed record ModelChoice(string Name, bool IsCloud)
{
    public string Tag => IsCloud ? "cloud · uses tokens" : "local · free";
}

/// <summary>A project's snapshots, as one group in the (grouped) snapshots panel. Derives from
/// <see cref="List{T}"/> so a <see cref="Microsoft.UI.Xaml.Data.CollectionViewSource"/> can group
/// on it directly — the ListView's group-header template binds to <see cref="Project"/> and
/// <see cref="Count"/>.</summary>
public sealed class SnapshotGroup : List<Services.ContextSnapshot>
{
    public SnapshotGroup(string project, IEnumerable<Services.ContextSnapshot> items) : base(items)
        => Project = project;

    public string Project { get; }

    /// <summary>Whether the group's Expander is open. Set when the groups are rebuilt (from the
    /// remembered collapsed-set) and read once via a OneTime x:Bind — the Expander's own
    /// expand/collapse events keep the remembered set current thereafter.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>Label for the group's "delete everything shown here" button. Computed here rather
    /// than assembled in XAML so the count is exact; a OneTime binding is always current because the
    /// groups are rebuilt on every panel populate.</summary>
    public string DeleteAllLabel => $"Delete all {Count}";

    /// <summary>The group action only earns its space once there's more than one item — with a single
    /// card, that card's own Delete button already does the same job. Bound as Visibility rather than
    /// a bool because x:Bind does no implicit bool-to-Visibility conversion.</summary>
    public Visibility DeleteAllVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A project's closed conversations, as one collapsible group in the History panel —
/// the archive twin of <see cref="SnapshotGroup"/>.</summary>
public sealed class HistoryGroup : List<Services.SessionArchiveEntry>
{
    public HistoryGroup(string project, IEnumerable<Services.SessionArchiveEntry> items) : base(items)
        => Project = project;

    public string Project { get; }

    public bool IsExpanded { get; set; } = true;

    /// <summary>See <see cref="SnapshotGroup.DeleteAllLabel"/>.</summary>
    public string DeleteAllLabel => $"Delete all {Count}";

    /// <summary>See <see cref="SnapshotGroup.DeleteAllVisibility"/>.</summary>
    public Visibility DeleteAllVisibility => Count > 1 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// One note as the panel shows it: the note itself plus the search snippet that explains why it
/// matched. The snippet depends on the current query, not on the file, so it can't live on
/// <see cref="Services.NoteEntry"/> (a reading of disk) — same split as History's rows.
/// </summary>
public sealed class NoteRow
{
    public required Services.NoteEntry Note { get; init; }

    /// <summary>The matching line from the note's body, when the hit came from text the card doesn't
    /// already show. Empty otherwise — <see cref="Vis.WhenText"/> hides the quote block.</summary>
    public string MatchSnippet { get; init; } = "";

    // Proxies, so the card template reads like the other two panels' cards.
    public string Title => Note.Title;
    public string FileName => Note.FileName;
    public string Preview => Note.Preview;
    public string TimeLabel => Note.TimeLabel;
    public string SizeLabel => Note.SizeLabel;

    /// <summary>An empty note has no preview line to show; the placeholder keeps the card from
    /// collapsing into a bare title and says what it is.</summary>
    public string PreviewOrPlaceholder =>
        string.IsNullOrWhiteSpace(Preview) ? "(empty — nothing written yet)" : Preview;
}

/// <summary>A project's notes, as one collapsible group in the Notes panel — the third of the
/// grouped-by-project panels, alongside <see cref="SnapshotGroup"/> and <see cref="HistoryGroup"/>.
///
/// Deliberately WITHOUT the "Delete all n" group action those two carry. A snapshot or an archived
/// conversation is a derived artifact the app made; a note is something the user wrote by hand, and
/// one button that deletes a folder's worth of writing is a different class of risk.</summary>
public sealed class NoteGroup : List<NoteRow>
{
    public NoteGroup(string project, IEnumerable<NoteRow> items) : base(items) => Project = project;

    public string Project { get; }

    public bool IsExpanded { get; set; } = true;
}

/// <summary>
/// One tile in the Appearance page's shipped-background gallery. Selection is baked in at build
/// time rather than observed: <c>UpdateBgControls</c> rebuilds the whole list on every change, the
/// same way the History and Notes panels rebuild their rows, so OneTime bindings are enough and
/// there's no INotifyPropertyChanged to keep honest.
/// </summary>
public sealed class BackgroundChoiceVm
{
    public required Services.BuiltInBackground Item { get; init; }

    /// <summary>True when this is the background currently in use.</summary>
    public required bool IsSelected { get; init; }

    public string DisplayName => Item.DisplayName;

    /// <summary>
    /// Thumbnail for the tile. <c>DecodePixelWidth</c> is set BEFORE the source (WinUI ignores it
    /// afterward) so a 1920-wide wallpaper decodes at tile size — otherwise every gallery image
    /// would sit in memory at full resolution just to draw a 128px card.
    /// </summary>
    public ImageSource Thumbnail
    {
        get
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 280 };
            bitmap.UriSource = new Uri(Item.FullPath);
            return bitmap;
        }
    }

    public Visibility CheckVisibility => Vis.When(IsSelected);

    /// <summary>An accent ring marks the active tile; the others take the ordinary border. Both are
    /// the shared brush INSTANCES, so they recolor with the theme like everything else.</summary>
    public Brush RingBrush => (Brush)Application.Current.Resources[
        IsSelected ? "MandoAccentBrush" : "MandoBorderBrush"];

    public Thickness RingThickness => new(IsSelected ? 2 : 1);
}

/// <summary>
/// x:Bind function-binding helpers. These exist so bool/string→<see cref="Visibility"/> logic can
/// stay OUT of the persisted service models: `SessionArchiveStore.cs` and friends are compiled into
/// the WinUI-free test project, so a Visibility property on them would break that build. Cheaper
/// than a converter registered in resources, and readable at the binding site.
/// </summary>
public static class Vis
{
    public static Visibility When(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility WhenText(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Row model for diff lines shown in the approval overlay.</summary>
public sealed class DiffLineVm
{
    public string Text { get; init; } = "";
    public SolidColorBrush Brush { get; init; } = new(Colors.Gray);
}

/// <summary>Row model for the MCP servers page.</summary>
public sealed class McpRow
{
    public string Name { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Status { get; init; } = "";
    public SolidColorBrush StatusBrush { get; init; } = new(Colors.Gray);
    /// <summary>Per-server on/off (the config's Disabled flag, inverted). Shared by every agent.</summary>
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>One choice in a Skills or MCP tag filter. A null tag means no tag constraint.</summary>
public sealed class TagFilterOption
{
    public string Label { get; init; } = "All tags";
    public string? Tag { get; init; }
}

/// <summary>A section of the skills list (e.g. "Enabled (12)"). A List subclass so a
/// CollectionViewSource can group on it directly; the header binds to <see cref="Key"/>.</summary>
public sealed class SkillRowGroup : List<SkillRow>
{
    public string Key { get; }
    public SkillRowGroup(string key, IEnumerable<SkillRow> items) : base(items) => Key = key;
}

/// <summary>A section of the MCP servers list (e.g. "Disabled (3)").</summary>
public sealed class McpRowGroup : List<McpRow>
{
    public string Key { get; }
    public McpRowGroup(string key, IEnumerable<McpRow> items) : base(items) => Key = key;
}

/// <summary>Row model for the global-skills page. FolderPath rides along so per-row actions
/// (the enable toggle) can act on the right skill without leaning on list selection.</summary>
public sealed class SkillRow
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];

    // Size of the instructions body — what gets injected into the prompt on load, so it's the cost
    // that spins a local model up. ~4 chars/token is the usual rough estimate.
    public int ApproxTokens => (Body.Length + 3) / 4;
    public bool IsLarge => ApproxTokens >= 2000;
    public string SizeLabel =>
        (ApproxTokens >= 1000 ? $"≈{ApproxTokens / 1000.0:0.0}k tok" : $"≈{ApproxTokens} tok")
        + (IsLarge ? " · large" : "");
    public SolidColorBrush SizeBrush =>
        new(ThemeManager.C(IsLarge ? ThemeManager.Current.Gold : ThemeManager.Current.Dim));
}

/// <summary>Chip model for the MCP editor's tool preview (test results).</summary>
public sealed class ToolChip
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Row model for the Appearance tab's theme picker — each card is drawn in
/// its own theme's colors so the list doubles as a preview.</summary>
public sealed class ThemeVm
{
    public required UiTheme Theme { get; init; }
    public string Name => Theme.Name;
    public string Description => Theme.Description;
    public SolidColorBrush BgBrush => new(ThemeManager.C(Theme.Background));
    public SolidColorBrush EdgeBrush => new(ThemeManager.C(Theme.Border));
    public SolidColorBrush FgBrush => new(ThemeManager.C(Theme.Text));
    public SolidColorBrush DimBrush => new(ThemeManager.C(Theme.Dim));
    public SolidColorBrush AccentBrush => new(ThemeManager.C(Theme.Accent));
    public SolidColorBrush GoldBrush => new(ThemeManager.C(Theme.Gold));
    public SolidColorBrush SkyBrush => new(ThemeManager.C(Theme.Sky));
    public SolidColorBrush GreenBrush => new(ThemeManager.C(Theme.Green));
}
