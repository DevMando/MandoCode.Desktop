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

public sealed partial class ChatTabView
{
    // ============================================================
    // Git status strip
    // ============================================================

    private int _branchRefreshSeq;
    private DateTime _lastBranchRefresh = DateTime.MinValue;
    private string? _lastGitRoot;
    private readonly ObservableCollection<GitChangeItem> _changes = new();

    /// <summary>Fire-and-forget refresh of the bottom status strip AND the explorer's Changes
    /// tab (one git call feeds both). Throttled (UpdateHeader runs on every controller state
    /// change) except when the root changed; sequence-guarded so an older, slower git call
    /// can never overwrite a newer result; any failure just hides the strip.</summary>
    private async void RefreshBranchChip(bool force = false)
    {
        var root = _controller.ProjectRootPath;
        if (root != _lastGitRoot) force = true;   // never show the previous folder's state
        if (!force && (DateTime.UtcNow - _lastBranchRefresh).TotalSeconds < 2) return;
        _lastBranchRefresh = DateTime.UtcNow;
        _lastGitRoot = root;

        var seq = ++_branchRefreshSeq;
        var info = await Task.Run(() => GitQuickStatus.TryGet(root));

        if (_shutDown || seq != _branchRefreshSeq) return;
        _lastGitInfo = info;
        UpdateChangesList(info, root);
        _wsTracker.CaptureBaselineIfPending(info);
        if (info == null)
        {
            StatusStrip.Visibility = Visibility.Collapsed;
            return;
        }

        BranchText.Text = info.Branch
            + (info.Ahead > 0 ? $" ↑{info.Ahead}" : "")
            + (info.Behind > 0 ? $" ↓{info.Behind}" : "");

        // One status light: conflicts trump dirty trumps clean.
        var (dotBrush, state) =
            info.Conflicted ? ("MandoRedBrush", "merge conflicts")
            : info.Dirty ? ("MandoGoldBrush", "uncommitted changes")
            : ("MandoGreenBrush", "clean");
        BranchDot.Fill = Application.Current.Resources[dotBrush] as Brush;

        var foreignRoot = info.RepoRoot.Length > 0 && !string.Equals(
            Path.TrimEndingDirectorySeparator(info.RepoRoot),
            Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        ToolTipService.SetToolTip(StatusStrip,
            (info.Detached ? "Detached HEAD at commit " + info.Branch : "Git branch: " + info.Branch)
            + " — " + state
            + (info.Ahead > 0 || info.Behind > 0
                ? $" ({info.Ahead} ahead, {info.Behind} behind upstream)" : "")
            // Git found the repo in an ANCESTOR folder — say so, or this reads as a ghost.
            + (foreignRoot ? $"\nRepository root: {info.RepoRoot} (this folder is inside that repository)" : ""));
        StatusStrip.Visibility = Visibility.Visible;
    }

    /// <summary>Rebuilds the Changes tab's rows from a fresh git snapshot (UI thread).</summary>
    private void UpdateChangesList(GitBranchInfo? info, string root)
    {
        if (ChangesList.ItemsSource == null) ChangesList.ItemsSource = _changes;

        // Rebuilding the collection re-realizes every ListView row — a visible flash — so
        // bail when this snapshot is identical to what's already shown (the common case:
        // most refreshes confirm state rather than change it). Badges derive from the same
        // data, so they can't have changed either.
        var incoming = info?.Changes ?? (IReadOnlyList<GitChangeEntry>)Array.Empty<GitChangeEntry>();
        if (incoming.Count == _changes.Count)
        {
            var identical = true;
            for (var i = 0; i < incoming.Count; i++)
            {
                if (incoming[i].RelPath != _changes[i].RelPath || incoming[i].Kind != _changes[i].Kind)
                {
                    identical = false;
                    break;
                }
            }
            if (identical) return;
        }

        _changes.Clear();
        if (info != null)
        {
            foreach (var c in info.Changes)
            {
                var relNative = c.RelPath.Replace('/', Path.DirectorySeparatorChar);
                _changes.Add(new GitChangeItem
                {
                    Kind = c.Kind,
                    KindBrush = BrushForKind(c.Kind),
                    KindLabel = c.Kind switch
                    {
                        "!" => "Merge conflict",
                        "U" => "Untracked (new, not yet added)",
                        "A" => "Added",
                        "D" => "Deleted",
                        "R" => "Renamed",
                        _ => "Modified",
                    },
                    Name = Path.GetFileName(c.RelPath.TrimEnd('/')),
                    Dir = Path.GetDirectoryName(relNative)?.Replace(Path.DirectorySeparatorChar, '/') ?? "",
                    FullPath = Path.Combine(root, relNative),
                    RelPath = c.RelPath,
                    TagTooltip = $"Tag in prompt — inserts @{c.RelPath}",
                });
            }
        }

        ChangesTabButton.Content = _changes.Count > 0 ? $"Changes ({_changes.Count})" : "Changes";
        ChangesEmptyText.Visibility = _changesTabActive && _changes.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        CommitButton.IsEnabled = _changes.Count > 0;

        RebuildDirtySets(info);
        RefreshExplorerDirtyFlags();
    }

    // --- dirty badges on the file tree ---
    // A changed file gets a gold dot; every ancestor folder gets one too, so a collapsed
    // folder still signals "something inside changed" (VS Code's badge behavior).

    private readonly HashSet<string> _gitDirtyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gitDirtyDirs = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildDirtySets(GitBranchInfo? info)
    {
        _gitDirtyFiles.Clear();
        _gitDirtyDirs.Clear();
        if (info == null) return;
        foreach (var c in info.Changes)
        {
            var rel = c.RelPath.TrimEnd('/');
            // Untracked directories arrive as one "dir/" entry — that's a dir badge, not a file.
            if (c.RelPath.EndsWith('/')) _gitDirtyDirs.Add(rel);
            else _gitDirtyFiles.Add(rel);
            for (var slash = rel.LastIndexOf('/'); slash > 0; slash = rel.LastIndexOf('/'))
            {
                rel = rel[..slash];
                _gitDirtyDirs.Add(rel);
            }
        }
    }

    /// <summary>Re-flags every REALIZED tree node in place (expansion state survives).
    /// Nodes created later pick their flag up at creation in LoadChildNodes.</summary>
    private void RefreshExplorerDirtyFlags()
    {
        Walk(ExplorerTree.RootNodes);

        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Content is ExplorerItem item) item.Dirty = IsItemDirty(item);
                if (node.Children.Count > 0) Walk(node.Children);
            }
        }
    }

    private bool IsItemDirty(ExplorerItem item) =>
        item.IsDirectory ? _gitDirtyDirs.Contains(item.RelPath) : _gitDirtyFiles.Contains(item.RelPath);

    private static Brush? BrushForKind(string kind) =>
        Application.Current.Resources[kind switch
        {
            "!" or "D" => "MandoRedBrush",
            "A" or "U" => "MandoGreenBrush",
            "R" => "MandoSkyBrush",
            _ => "MandoGoldBrush",
        }] as Brush;

    private void UpdatePlanProgress(int done, int total, bool active)
    {
        PlanProgressPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (total > 0)
        {
            PlanProgressBar.Value = done * 100.0 / total;
            PlanProgressText.Text = $"Plan: step {Math.Min(done + 1, total)} of {total}";
        }
    }

    /// <summary>
    /// Populates the model dropdown each time it opens. The flyout appears immediately showing a
    /// loading spinner; this awaits the model list off the UI thread and swaps in the rows (or an
    /// inline error) when it returns. Tab-local — picking a model repins THIS agent only.
    /// </summary>
    private async void ModelFlyout_Opening(object? sender, object e)
    {
        ModelLoadingPanel.Visibility = Visibility.Visible;
        ModelErrorText.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Collapsed;

        var result = await _controller.LoadAvailableModelsAsync();

        if (!result.Ok)
        {
            ModelErrorText.Text = result.Error;
            ModelLoadingPanel.Visibility = Visibility.Collapsed;
            ModelErrorText.Visibility = Visibility.Visible;
            return;
        }

        var sky = (Brush)Application.Current.Resources["MandoSkyBrush"];
        var dim = (Brush)Application.Current.Resources["MandoDimBrush"];
        var badgeBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        var current = _controller.ModelName;

        var items = result.Models.Select(m =>
        {
            var cloud = MandoCodeConfig.IsCloudModel(m);
            return new ModelItem(m, cloud ? "cloud" : "local", cloud ? sky : dim, badgeBg);
        }).ToList();

        ModelList.ItemsSource = items;
        ModelList.SelectedItem = items.FirstOrDefault(
            i => string.Equals(i.Name, current, StringComparison.OrdinalIgnoreCase));

        ModelLoadingPanel.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Visible;
    }

    private async void ModelList_ItemClick(object sender, ItemClickEventArgs e)
    {
        ModelFlyout.Hide();
        if (e.ClickedItem is not ModelItem item) return;
        if (string.Equals(item.Name, _controller.ModelName, StringComparison.OrdinalIgnoreCase)) return;

        await Task.Run(() => _controller.SelectModelAsync(item.Name));
        UpdateHeader();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            // Without this, every FolderPicker/FileOpenPicker/FileSavePicker in the app shares
            // ONE "last visited folder" bucket — picking a folder anywhere (e.g. the Music
            // flyout's playlist picker) silently becomes the starting point here too. Each
            // call site across the app gets its own identifier for exactly this reason.
            SettingsIdentifier = "ProjectRoot",
        };
        picker.FileTypeFilter.Add("*");

        // Unpackaged apps must initialize pickers with the window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _transcript.Append(_html.Info($"Project root changed to: {folder.Path}"));
        _transcript.Append(_html.Dim("Rebuilding the AI session for the new project…"));

        // Retargets THIS tab only — its own ProjectRootAccessor, file cache, and kernel.
        // Other agents keep working in their own folders.
        var session = Session;
        await Task.Run(async () =>
        {
            await session.ChangeProjectRootAsync(folder.Path);
            _transcript.Append(_html.Success("✓ Ready."));
        });
        UpdateHeader();
        if (_explorerOpen) BuildExplorerRoot();   // the open tree must follow the new root
    }

    // ============================================================
    // File explorer panel
    // ============================================================

    private bool _explorerOpen;
    private string? _explorerRoot;   // root the tree was last built for

    private void ExplorerButton_Click(object sender, RoutedEventArgs e) => ToggleExplorer(!_explorerOpen);
    private void ExplorerClose_Click(object sender, RoutedEventArgs e) => ToggleExplorer(false);

    private void ExplorerRefresh_Click(object sender, RoutedEventArgs e)
    {
        BuildExplorerRoot();
        RefreshBranchChip(force: true);   // the Changes tab re-reads too
    }

    // --- Files / Changes tabs ---

    private bool _changesTabActive;

    private void FilesTab_Click(object sender, RoutedEventArgs e) => SetExplorerTab(changes: false);
    private void ChangesTab_Click(object sender, RoutedEventArgs e) => SetExplorerTab(changes: true);

    private void SetExplorerTab(bool changes)
    {
        _changesTabActive = changes;
        ExplorerTree.Visibility = changes ? Visibility.Collapsed : Visibility.Visible;
        ChangesList.Visibility = changes ? Visibility.Visible : Visibility.Collapsed;
        ChangesEmptyText.Visibility = changes && _changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesFooter.Visibility = changes ? Visibility.Visible : Visibility.Collapsed;
        CommitButton.IsEnabled = _changes.Count > 0;
        FilesTabButton.FontWeight = changes ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        ChangesTabButton.FontWeight = changes ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        FilesTabButton.Opacity = changes ? 0.55 : 1;
        ChangesTabButton.Opacity = changes ? 1 : 0.55;
    }

    private void ChatRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_explorerOpen) SizeExplorer();
    }

    private void SizeExplorer()
    {
        // Default ~20% of the window, clamped so the tree stays usable on small windows and
        // doesn't waste half a 4K monitor on the other end. Once the user has dragged the
        // splitter, their width wins (re-clamped so a shrunken window can't strand the panel).
        var w = ChatRoot.ActualWidth;
        if (w <= 0) return;
        var target = _explorerUserWidth ?? Math.Clamp(w * 0.20, 220, 460);
        ExplorerPanel.Width = Math.Clamp(target, MinExplorerWidth, MaxExplorerWidth());
    }

    private const double MinExplorerWidth = 180;
    private double MaxExplorerWidth() => Math.Max(MinExplorerWidth, ChatRoot.ActualWidth * 0.6);

    // --- splitter drag (same pointer-capture pattern as MainWindow's terminal splitter) ---

    private double? _explorerUserWidth;   // set on first drag; SizeExplorer defers to it
    private bool _draggingExplorer;
    private double _explorerDragStartWidth;
    private double _explorerDragStartX;

    private void ExplorerSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingExplorer = true;
        _explorerDragStartWidth = ExplorerPanel.ActualWidth;
        _explorerDragStartX = e.GetCurrentPoint(ChatRoot).Position.X;   // stable frame while the grip moves
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void ExplorerSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingExplorer) return;
        // Dragging left grows the panel; right shrinks it.
        var delta = e.GetCurrentPoint(ChatRoot).Position.X - _explorerDragStartX;
        var next = Math.Clamp(_explorerDragStartWidth - delta, MinExplorerWidth, MaxExplorerWidth());
        ExplorerPanel.Width = next;
        _explorerUserWidth = next;
    }

    private void ExplorerSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingExplorer) return;
        _draggingExplorer = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void ToggleExplorer(bool open)
    {
        if (open == _explorerOpen) return;
        _explorerOpen = open;

        // Docked, not overlaid: the panel sits in the transcript row's second column, so
        // showing it RESIZES the transcript (text stays fully readable) and collapsing it
        // gives the width back. No slide animation — animating a WebView2's width forces
        // continuous relayout of the browser surface, and instant dock/undock is how
        // solution-explorer-style panels behave anyway.
        if (open)
        {
            SizeExplorer();
            // (Re)build on open when the tab's root changed since the tree was built — the
            // panel keeps its expansion state across close/open within the same root.
            if (_explorerRoot != _controller.ProjectRootPath) BuildExplorerRoot();
            ExplorerPanel.Visibility = Visibility.Visible;
            ExplorerSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            ExplorerPanel.Visibility = Visibility.Collapsed;
            ExplorerSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildExplorerRoot()
    {
        _explorerRoot = _controller.ProjectRootPath;
        ExplorerRootText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(_explorerRoot));
        ToolTipService.SetToolTip(ExplorerRootText, _explorerRoot);
        ExplorerTree.RootNodes.Clear();
        foreach (var node in LoadChildNodes(_explorerRoot)) ExplorerTree.RootNodes.Add(node);
        StartExplorerWatcher(_explorerRoot);
    }

    // --- filesystem watcher: the tree follows external creates/deletes/renames on its own ---
    // Efficiency comes from three choices: (1) only NAME notifications — content writes don't
    // change tree shape; (2) events debounce into one flush, so a build touching 500 files
    // costs one pass; (3) a flush re-syncs only REALIZED directory nodes — churn under a
    // never-expanded folder (node_modules, bin/obj) is a hash lookup and a skip, because
    // lazy loading will read the truth from disk whenever it's finally expanded.

    private FileSystemWatcher? _fsWatcher;
    private readonly object _fsLock = new();
    private readonly HashSet<string> _pendingFsDirs = new(StringComparer.OrdinalIgnoreCase);
    private bool _fsFlushQueued;
    private bool _fsSyncAll;   // watcher buffer overflowed — re-sync every realized dir

    private void StartExplorerWatcher(string root)
    {
        StopExplorerWatcher();
        try
        {
            _fsWatcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                // LastWrite so EDITS refresh git state (M rows, badges, dirty dot) — name
                // events alone only cover tree shape. Content writes are routed git-only
                // below: they can't change the tree, so they never trigger tree syncs.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,   // max — fewer overflows during big builds
            };
            _fsWatcher.Created += (_, e) => QueueFsEvent(e.FullPath);
            _fsWatcher.Deleted += (_, e) => QueueFsEvent(e.FullPath);
            _fsWatcher.Renamed += (_, e) => { QueueFsEvent(e.OldFullPath); QueueFsEvent(e.FullPath); };
            _fsWatcher.Changed += (_, e) => QueueFsEvent(e.FullPath, treeRelevant: false);
            _fsWatcher.Error += (_, _) => { lock (_fsLock) { _fsSyncAll = true; } QueueFsEvent(root); };
            _fsWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            _fsWatcher = null;   // best-effort — the refresh button still exists
        }
    }

    private void StopExplorerWatcher()
    {
        try { _fsWatcher?.Dispose(); } catch { }
        _fsWatcher = null;
    }

    /// <summary>Threadpool-side: coalesce this event's parent directory into the pending set
    /// and arm one debounced flush. .git churn and content-only writes skip the tree but
    /// still refresh git state — that's how external edits, branch switches, and commits
    /// show up without a manual refresh.</summary>
    private void QueueFsEvent(string fullPath, bool treeRelevant = true)
    {
        bool arm;
        lock (_fsLock)
        {
            var rel = ToRelOrNull(fullPath)?.Replace('\\', '/');
            if (rel == null) return;
            var isGit = rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase);

            // Our OWN git calls write .git/index (+ transient *.lock files) — reacting to
            // those would refresh forever: refresh → git status → index event → refresh…
            // Ignore them; real external actions (checkout, commit) also touch HEAD/refs,
            // which still get through and trigger the refresh we want.
            if (isGit && (rel.EndsWith("/index", StringComparison.OrdinalIgnoreCase)
                       || rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
                return;

            if (!isGit && treeRelevant)
                _pendingFsDirs.Add(Path.GetDirectoryName(fullPath) ?? "");

            // Workspace notes: remember WHICH files were touched while the agent was idle.
            // Status-snapshot diffing alone misses content edits to files that were ALREADY
            // dirty/untracked (their status entry doesn't change) — this set fills that gap.
            // Idle-gated so the agent's own writes never count as external.
            if (!isGit && !_controller.IsProcessing)
                _wsTracker.RecordTouch(rel);

            arm = !_fsFlushQueued;
            _fsFlushQueued = true;
        }
        if (arm) _ = FlushFsEventsAsync();

        string? ToRelOrNull(string p)
        {
            var root = _explorerRoot;
            if (root == null) return null;
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? p[prefix.Length..] : null;
        }
    }

    private async Task FlushFsEventsAsync()
    {
        await Task.Delay(800);   // coalesce the burst
        List<string> dirs;
        bool syncAll;
        lock (_fsLock)
        {
            syncAll = _fsSyncAll;
            _fsSyncAll = false;
            dirs = _pendingFsDirs.ToList();
            _pendingFsDirs.Clear();
            _fsFlushQueued = false;
        }
        OnUi(() =>
        {
            if (_shutDown) return;
            if (syncAll) SyncAllRealizedDirs();
            else foreach (var dir in dirs) SyncRealizedDir(dir);
            RefreshBranchChip(force: true);   // badges, Changes tab, and status strip follow
        });
    }

    /// <summary>Re-syncs one directory's children IF that directory is realized in the tree;
    /// unexpanded directories are skipped (lazy load reads fresh from disk anyway).</summary>
    private void SyncRealizedDir(string dir)
    {
        var list = FindRealizedChildList(dir);
        if (list != null) SyncDirectoryNode(list, dir);
    }

    private void SyncAllRealizedDirs()
    {
        var root = _explorerRoot;
        if (root == null) return;
        SyncDirectoryNode(ExplorerTree.RootNodes, root);
        Walk(ExplorerTree.RootNodes);

        void Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is { HasUnrealizedChildren: false, Content: ExplorerItem { IsDirectory: true } item })
                {
                    SyncDirectoryNode(n.Children, item.FullPath);
                    Walk(n.Children);
                }
            }
        }
    }

    private IList<TreeViewNode>? FindRealizedChildList(string dir)
    {
        var root = _explorerRoot;
        if (root == null) return null;
        if (PathsEqual(dir, root)) return ExplorerTree.RootNodes;
        return Find(ExplorerTree.RootNodes);

        IList<TreeViewNode>? Find(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Content is ExplorerItem { IsDirectory: true } item && PathsEqual(item.FullPath, dir))
                    return n.HasUnrealizedChildren ? null : n.Children;
                if (n.Children.Count > 0)
                {
                    var found = Find(n.Children);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static bool PathsEqual(string a, string b) => string.Equals(
            Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Minimal diff of a realized directory node against disk: remove rows whose
    /// path vanished, insert new rows at their sorted position. Never rebuilds surviving
    /// nodes, so expansion state below them is preserved.</summary>
    private void SyncDirectoryNode(IList<TreeViewNode> children, string dir)
    {
        var root = _explorerRoot ?? _controller.ProjectRootPath;
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception) { return; }
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var desired = new List<(string Path, bool IsDir)>(dirs.Length + files.Length);
        foreach (var d in dirs) desired.Add((d, true));
        foreach (var f in files) desired.Add((f, false));
        var desiredSet = new HashSet<string>(desired.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);

        for (var i = children.Count - 1; i >= 0; i--)
            if (children[i].Content is ExplorerItem it && !desiredSet.Contains(it.FullPath))
                children.RemoveAt(i);

        var existing = new HashSet<string>(
            children.Select(n => (n.Content as ExplorerItem)?.FullPath ?? ""),
            StringComparer.OrdinalIgnoreCase);

        for (var idx = 0; idx < desired.Count; idx++)
        {
            var (path, isDir) = desired[idx];
            if (existing.Contains(path)) continue;
            var item = isDir ? ExplorerItem.ForFolder(path, root) : ExplorerItem.ForFile(path, root);
            item.Dirty = IsItemDirty(item);
            var node = new TreeViewNode { Content = item };
            if (isDir) node.HasUnrealizedChildren = true;
            children.Insert(Math.Min(idx, children.Count), node);
        }
    }

    /// <summary>One directory level, folders first then files, both alphabetical. Unreadable
    /// or vanished directories render as empty rather than throwing.</summary>
    private List<TreeViewNode> LoadChildNodes(string dir)
    {
        var root = _explorerRoot ?? _controller.ProjectRootPath;
        var nodes = new List<TreeViewNode>();
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception) { return nodes; }
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
        {
            var item = ExplorerItem.ForFolder(d, root);
            item.Dirty = IsItemDirty(item);
            nodes.Add(new TreeViewNode { Content = item, HasUnrealizedChildren = true });
        }
        foreach (var f in files)
        {
            var item = ExplorerItem.ForFile(f, root);
            item.Dirty = IsItemDirty(item);
            nodes.Add(new TreeViewNode { Content = item });
        }
        return nodes;
    }

    /// <summary>The row's @ button — shared by the file tree (TreeViewNode rows) and the
    /// Changes list (GitChangeItem rows): tags the file/folder in the prompt, identical
    /// result to dragging the row onto the input box.</summary>
    private void ExplorerTag_Click(object sender, RoutedEventArgs e)
    {
        var ctx = (sender as FrameworkElement)?.DataContext;
        var path = ctx switch
        {
            TreeViewNode { Content: ExplorerItem item } => item.FullPath,
            GitChangeItem change => change.FullPath,
            _ => null,
        };
        if (path != null) InsertFileTokens(new[] { path });
    }

    private void ChangesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<GitChangeItem>().Select(c => c.FullPath).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        e.Data.SetText(string.Join("\n", paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    /// <summary>The row's ± button: show this file's diff as a transcript DiffCard. An
    /// explicit button (not row click) so selecting or starting a drag never spawns a card,
    /// and no click-vs-double-click disambiguation delay is needed.</summary>
    private async void ChangesDiff_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GitChangeItem item || _shutDown) return;

        var root = _controller.ProjectRootPath;
        var diff = await Task.Run(() => GitQuickStatus.TryGetDiff(root, item.RelPath, untracked: item.Kind == "U"));
        if (_shutDown) return;

        if (diff == null)
            _transcript.Append(_html.Warn($"Couldn't get a diff for {item.RelPath}"));
        else if (diff.Lines.Count == 0)
            _transcript.Append(_html.Dim($"{item.RelPath}: {diff.Summary}"));
        else
            _transcript.Append(_html.DiffCard(item.RelPath, diff.Lines, diff.Summary, interactive: true));
    }

    /// <summary>Pre-fills the prompt with a commit request — never sends, never commits.
    /// Caret-aware insert, so tagging files first then clicking Commit… composes naturally
    /// ("@a.cs @b.cs Commit the current changes…"). The user can edit, then sends; the
    /// bottom-bar approval gates the actual git command.</summary>
    private void Commit_Click(object sender, RoutedEventArgs e) =>
        InsertAtCaret("Commit the current changes with an appropriate message");

    private void ChangeUndo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GitChangeItem item)
            UndoFileFromCard(item.RelPath);
    }

    /// <summary>Fire-and-forget bridge for non-async call sites (web message handler, row
    /// button). async void is safe here: ConfirmAndUndoAsync catches nothing fatal — git
    /// failure is reported to the transcript, not thrown.</summary>
    private async void UndoFileFromCard(string relPath) => await ConfirmAndUndoAsync(relPath);

    /// <summary>The one destructive action in the app, so it always confirms first —
    /// whether it came from a Changes row or a diff card's Undo chip.</summary>
    private async Task ConfirmAndUndoAsync(string relPath)
    {
        var dialog = new ContentDialog
        {
            Title = "Discard changes?",
            Content = $"{relPath} will be restored to its state at the last commit. This can't be undone.",
            PrimaryButtonText = "Discard changes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var root = _controller.ProjectRootPath;
        var ok = await Task.Run(() => GitQuickStatus.TryUndoChanges(root, relPath));
        if (_shutDown) return;
        _transcript.Append(ok
            ? _html.Success($"Restored {relPath} to its state at the last commit.")
            : _html.Warn($"Couldn't restore {relPath} — is it still tracked by git?"));
        if (ok)
        {
            // Tell the model explicitly — discarding its work is feedback, not just a file
            // event — and re-baseline so the generic delta doesn't report it a second time.
            _controller.NoteWorkspaceEvent(
                $"The user DISCARDED all uncommitted changes to {relPath} (restored to the last commit). " +
                "If you changed that file earlier, those changes are gone by the user's choice — don't re-apply them unless asked.");
            _wsTracker.MarkCapturePending();
        }
        RefreshBranchChip(force: true);
    }

    private void ChangesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not GitChangeItem item) return;
        if (!File.Exists(item.FullPath)) return;   // deleted entries have nothing to open
        if (ShellOpen.Try(item.FullPath) is { } ex)
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
    }

    private void ExplorerTag_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ((UIElement)sender).Opacity = 1;

    private void ExplorerTag_PointerExited(object sender, PointerRoutedEventArgs e)
        => ((UIElement)sender).Opacity = 0.45;

    private void ExplorerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!args.Node.HasUnrealizedChildren) return;
        args.Node.HasUnrealizedChildren = false;
        if (args.Node.Content is not ExplorerItem item || !item.IsDirectory) return;
        foreach (var child in LoadChildNodes(item.FullPath)) args.Node.Children.Add(child);
    }

    private void ExplorerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // Single click: folders toggle, files only select. Opening is double-click territory
        // (ExplorerTree_DoubleTapped) — a stray single click must never launch an app.
        if (args.InvokedItem is TreeViewNode { Content: ExplorerItem { IsDirectory: true } } node)
            node.IsExpanded = !node.IsExpanded;
    }

    private void ExplorerTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // The template's elements inherit the row's TreeViewNode as DataContext. Files only:
        // for folders double-click fights the expand/collapse toggle — they open externally
        // via the row's open button (ExplorerOpen_Click) instead.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TreeViewNode node ||
            node.Content is not ExplorerItem { IsDirectory: false } item)
            return;
        if (ShellOpen.Try(item.FullPath) is { } ex)
            _transcript.Append(_html.Warn($"Couldn't open file: {ex.Message}"));
    }

    /// <summary>The row's open button: folders in Windows File Explorer, files in their
    /// default app — ShellExecute either way.</summary>
    private void ExplorerOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TreeViewNode
            { Content: ExplorerItem item }) return;
        if (ShellOpen.Try(item.FullPath) is { } ex)
            _transcript.Append(_html.Warn(
                $"Couldn't open {(item.IsDirectory ? "folder" : "file")}: {ex.Message}"));
    }

    // ============================================================
    // Drag & drop @-references
    // ============================================================

    /// <summary>Dragging explorer rows carries their full paths as text — the input box's
    /// Drop handler recognizes existing paths and converts them to @tokens.</summary>
    private void ExplorerTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        var paths = args.Items.OfType<TreeViewNode>()
            .Select(n => n.Content).OfType<ExplorerItem>()
            .Select(i => i.FullPath).ToList();
        if (paths.Count == 0) { args.Cancel = true; return; }
        args.Data.SetText(string.Join("\n", paths));
        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void InputBox_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    // --- drop-to-tag overlay choreography ---
    // Show when a drag enters the tab: over XAML chrome that's ChatRoot's DragEnter; over the
    // WebView it's the transcript script's 'drag-enter' message (Chromium owns drags there).
    // Hide when the drag leaves the overlay/tab or when any drop completes. Moving between
    // those regions can flicker the overlay off/on for a frame — harmless.

    private void ShowDropOverlay() => DropOverlay.Visibility = Visibility.Visible;
    private void HideDropOverlay() => DropOverlay.Visibility = Visibility.Collapsed;

    private void ChatRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
            ShowDropOverlay();
    }

    private void ChatRoot_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();
    private void DropOverlay_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();

    private void DropOverlay_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) ||
            e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void DropOverlay_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        await HandleDropAsync(e);
    }

    private async void InputBox_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        await HandleDropAsync(e);
    }

    /// <summary>Shared drop handling for the input box and the drop-to-tag overlay: paths
    /// become @tokens, ordinary text inserts as text.</summary>
    private async Task HandleDropAsync(DragEventArgs e)
    {
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                // Shell drop (Windows Explorer): real files/folders with paths.
                var items = await e.DataView.GetStorageItemsAsync();
                InsertFileTokens(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                // Text drop: explorer-tree rows arrive as newline-joined full paths. If every
                // line is an existing path, tokenize; otherwise it's ordinary dragged text.
                var text = await e.DataView.GetTextAsync();
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 0 && lines.All(l => File.Exists(l) || Directory.Exists(l)))
                    InsertFileTokens(lines);
                else
                    InsertAtCaret(text);
            }
        }
        catch (Exception ex)
        {
            _transcript.Append(_html.Warn($"Couldn't read the dropped item: {ex.Message}"));
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Converts full paths into the same @tokens the autocomplete inserts: project-root
    /// relative, forward slashes, trailing '/' for folders. Items outside this tab's project
    /// root can't be resolved by the @ pipeline, so they're skipped with a warning.</summary>
    private void InsertFileTokens(IEnumerable<string> fullPaths)
    {
        var root = _controller.ProjectRootPath;
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var tokens = new List<string>();
        var outside = new List<string>();

        foreach (var raw in fullPaths)
        {
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { continue; }
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                outside.Add(full);
                continue;
            }
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            tokens.Add("@" + rel + (Directory.Exists(full) ? "/" : ""));
        }

        if (tokens.Count > 0)
            InsertAtCaret(string.Join(" ", tokens) + " ");
        if (outside.Count > 0)
            _transcript.Append(_html.Warn(
                $"Skipped {outside.Count} dropped item{(outside.Count == 1 ? "" : "s")} outside this tab's project folder — @ references only work under {root}"));
    }

    /// <summary>Inserts at the caret with token-safe spacing: a separating space is added when
    /// the caret touches non-whitespace, so a dropped @token never glues onto existing text.</summary>
    private void InsertAtCaret(string insert)
    {
        var text = InputBox.Text;
        var caret = Math.Clamp(InputBox.SelectionStart, 0, text.Length);
        if (caret > 0 && !char.IsWhiteSpace(text[caret - 1])) insert = " " + insert;
        InputBox.Text = text[..caret] + insert + text[caret..];
        InputBox.SelectionStart = caret + insert.Length;
        InputBox.Focus(FocusState.Programmatic);
    }

}
