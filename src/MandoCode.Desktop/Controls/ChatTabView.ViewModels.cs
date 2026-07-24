using System.Collections.ObjectModel;
using System.Text.Json;
using MandoCode.Models;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MandoCode.Desktop;

/// <summary>One row in the header's model dropdown: the model tag plus a cloud/local badge.
/// Built on the UI thread when the flyout opens, so it can carry ready-made brushes.</summary>
public sealed class ModelItem
{
    public ModelItem(string name, string badge, Brush badgeForeground, Brush badgeBackground)
    {
        Name = name;
        Badge = badge;
        BadgeForeground = badgeForeground;
        BadgeBackground = badgeBackground;
    }

    public string Name { get; }
    public string Badge { get; }
    public Brush BadgeForeground { get; }
    public Brush BadgeBackground { get; }
}

/// <summary>One row in the file-explorer tree. Folder nodes are created with unrealized
/// children and lazy-load their contents on first expand (ChatTabView.ExplorerTree_Expanding).</summary>
public sealed class ExplorerItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; private init; } = "";
    public string FullPath { get; private init; } = "";
    public bool IsDirectory { get; private init; }

    /// <summary>Root-relative path with forward slashes \u2014 the key used to match this row
    /// against git change entries.</summary>
    public string RelPath { get; private init; } = "";

    /// <summary>The exact @token the row produces (root-relative, forward slashes, trailing
    /// '/' on folders) \u2014 shown in the tag button's tooltip so hovering teaches the @ syntax.</summary>
    public string Token { get; private init; } = "";

    public string TagTooltip => $"Tag in prompt \u2014 inserts {Token}";

    /// <summary>Files: this file has uncommitted changes. Folders: something inside does.
    /// Mutable + observable so rows already realized in the tree light up in place when a
    /// git refresh lands (rebuilding the tree would lose expansion state).</summary>
    public bool Dirty
    {
        get => _dirty;
        set
        {
            if (_dirty == value) return;
            _dirty = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DirtyVisibility)));
        }
    }
    private bool _dirty;

    public Visibility DirtyVisibility => _dirty ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";   // folder / document

    /// <summary>Resolved per-realization from app resources, so icons pick up live theme
    /// switches the next time rows are created (matching how transcript colors retheme).</summary>
    public Brush? IconBrush =>
        Application.Current.Resources[IsDirectory ? "MandoGoldBrush" : "MandoDimBrush"] as Brush;

    public static ExplorerItem ForFolder(string path, string root)
    {
        var rel = Rel(path, root);
        return new() { Name = Path.GetFileName(path), FullPath = path, IsDirectory = true, RelPath = rel, Token = "@" + rel + "/" };
    }

    public static ExplorerItem ForFile(string path, string root)
    {
        var rel = Rel(path, root);
        return new() { Name = Path.GetFileName(path), FullPath = path, IsDirectory = false, RelPath = rel, Token = "@" + rel };
    }

    private static string Rel(string path, string root) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>One row in the explorer's Changes tab: a working-tree change with its display
/// letter/color, split name + directory, and the @token its tag button inserts. Built on
/// the UI thread from a GitQuickStatus snapshot, so it carries ready-made brushes
/// (same pattern as ModelItem).</summary>
public sealed class GitChangeItem
{
    public string Kind { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public Brush? KindBrush { get; init; }
    public string Name { get; init; } = "";
    public string Dir { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string RelPath { get; init; } = "";
    public string TagTooltip { get; init; } = "";

    /// <summary>Undo restores from HEAD, so it needs a HEAD side: hidden for untracked rows
    /// ("undoing" a new file would DELETE it — different action, different UI) and renamed
    /// rows (a clean rename-undo needs both paths).</summary>
    public Visibility UndoVisibility => Kind is "M" or "D" or "!" ? Visibility.Visible : Visibility.Collapsed;

    public string UndoTooltip => Kind == "D"
        ? "Restore this deleted file"
        : "Undo changes — restore this file to the last commit (asks first)";
}
