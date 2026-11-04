using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    // Views remain children of TabHost for their entire lifetime, including hidden workspaces.
    private sealed class DesktopWorkspace
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Main";
        public List<ChatTabEntry> Tabs { get; } = new();
        public ChatTabEntry? Selected { get; set; }
        public List<ChatTabEntry> SplitPanes { get; } = new();
        public List<double> Columns { get; set; } = new();
        public List<double> Rows { get; set; } = new();
        public Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? Button { get; set; }
        public Grid? Header { get; set; }
        public TextBlock? Label { get; set; }
        public Button? CloseButton { get; set; }
        public bool PointerOver { get; set; }
        public bool FocusWithin { get; set; }
    }

    private readonly List<DesktopWorkspace> _workspaces = new();
    private DesktopWorkspace _activeWorkspace = new();
    private IEnumerable<ChatTabEntry> AllTabs => _workspaces.SelectMany(w => w.Tabs);
    private bool _loadingWorkspaces = true;
    private bool _closingWorkspace;

    private static WorkspaceShape ShapeFor(DesktopWorkspace w) => new(
        w.Tabs.Select(t => new WorkspaceTabState(t.View.Session.Title,
            t.View.Session.ProjectRoot.ProjectRoot, PersistedModelFor(t), t.View.Session.PersistKey)).ToList(),
        w.Selected == null ? 0 : Math.Max(0, w.Tabs.IndexOf(w.Selected)),
        w.SplitPanes.Count >= 2 ? w.SplitPanes.Select(t => t.View.Session.PersistKey).ToList() : null,
        new(w.Columns), new(w.Rows));

    private void RestoreWorkspaces()
    {
        var saved = WorkspaceState.LoadAll();
        foreach (var item in saved.Workspaces)
        {
            var workspace = new DesktopWorkspace { Id = item.Id, Name = item.Name };
            _workspaces.Add(workspace);
            _activeWorkspace = workspace;
            var selectedTab = item.Shape.Tabs.ElementAtOrDefault(item.Shape.ActiveIndex);
            ChatTabEntry? restoredSelection = null;
            foreach (var tab in item.Shape.Tabs.Where(t => Directory.Exists(t.ProjectRoot)))
            {
                var entry = CreateChatTab(tab.ProjectRoot, tab.Title, tab.Model, tab.Key);
                if (ReferenceEquals(tab, selectedTab)) restoredSelection = entry;
            }
            if (_tabs.Count > 0) SelectTab(restoredSelection ?? _tabs[0]);
            RestoreSplitLayout(item.Shape);
        }
        if (_workspaces.Count == 0) _workspaces.Add(_activeWorkspace);
        if (!AllTabs.Any()) CreateChatTab();
        SwitchWorkspace(_workspaces.FirstOrDefault(w => w.Id == saved.ActiveWorkspaceId) ?? _workspaces[0]);
        _loadingWorkspaces = false;
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_closingWorkspace) return;
        var number = 1;
        while (_workspaces.Any(w => w.Name == $"Workspace {number}")) number++;
        var workspace = new DesktopWorkspace { Name = $"Workspace {number}" };
        _workspaces.Add(workspace);
        SwitchWorkspace(workspace);
        AddTab_Click(sender, e);
    }

    private void SwitchWorkspace(DesktopWorkspace workspace)
    {
        if (!_workspaces.Contains(workspace)) return;
        _activeWorkspace = workspace;
        if (_selected != null) SelectTab(_selected);
        else { _sessions.Activate(null); EnterEmptyState(); }
        RefreshWorkspaceStrip();
        SaveWorkspace();
        DispatcherQueue.TryEnqueue(() => workspace.Button?.StartBringIntoView());
    }

    private void RefreshWorkspaceStrip()
    {
        foreach (var workspace in _workspaces)
        {
            if (workspace.Button == null)
            {
                var header = new Grid { MaxWidth = 220 };
                var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                {
                    Padding = new Thickness(12, 6, 36, 6), MinWidth = 100,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Content = label,
                };
                workspace.Button = button;
                workspace.Header = header;
                workspace.Label = label;
                button.Click += (_, _) => SwitchWorkspace(workspace);
                // Sibling controls: closing an inactive workspace must not select it first.
                var closeButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
                    Width = 24, Height = 24, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                };
                workspace.CloseButton = closeButton;
                closeButton.Click += async (_, _) => await CloseWorkspaceAsync(workspace);
                header.Children.Add(button);
                header.Children.Add(closeButton);
                header.PointerEntered += (_, _) =>
                {
                    workspace.PointerOver = true;
                    UpdateWorkspaceCloseVisibility(workspace);
                };
                header.PointerExited += (_, _) =>
                {
                    workspace.PointerOver = false;
                    UpdateWorkspaceCloseVisibility(workspace);
                };
                header.GotFocus += (_, _) =>
                {
                    workspace.FocusWithin = true;
                    UpdateWorkspaceCloseVisibility(workspace);
                };
                header.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot);
                    workspace.FocusWithin = ReferenceEquals(focused, button) || ReferenceEquals(focused, closeButton);
                    UpdateWorkspaceCloseVisibility(workspace);
                });
                var menu = new MenuFlyout();
                var rename = new MenuFlyoutItem { Text = "Rename workspace" };
                rename.Click += async (_, _) => await RenameWorkspaceAsync(workspace);
                var close = new MenuFlyoutItem { Text = "Close workspace" };
                close.Click += async (_, _) => await CloseWorkspaceAsync(workspace);
                menu.Items.Add(rename);
                menu.Items.Add(close);
                button.ContextFlyout = menu;
                WorkspaceStrip.Children.Add(header);
            }
            var approval = workspace.Tabs.Any(t => t.View.IsApprovalOpen);
            var busy = workspace.Tabs.Any(t => t.View.Session.Controller.IsProcessing || t.View.Session.CommandLog.IsRunning);
            workspace.Button.IsChecked = workspace == _activeWorkspace;
            workspace.Label!.Text = workspace.Name + (approval ? "  · !" : busy ? "  · Working" : "");
            workspace.Button.BorderBrush = (Brush)Application.Current.Resources[
                workspace == _activeWorkspace ? "MandoAccentBrush" : "MandoBorderBrush"];
            workspace.Button.Foreground = (Brush)Application.Current.Resources[
                workspace == _activeWorkspace ? "MandoAccentBrush" : "MandoDimBrush"];
            workspace.Button.Background = (Brush)Application.Current.Resources[
                workspace == _activeWorkspace ? "MandoBackgroundBrush" : "MandoPanelBrush"];
            ToolTipService.SetToolTip(workspace.Button,
                $"{workspace.Name} · {workspace.Tabs.Count} agents · Right-click to rename or close" + (approval ? " · Approval waiting" : ""));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(workspace.Button,
                workspace.Name + (approval ? ", approval waiting" : busy ? ", working" : ""));
            workspace.CloseButton!.Foreground = workspace.Button.Foreground;
            ToolTipService.SetToolTip(workspace.CloseButton, $"Close {workspace.Name}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(workspace.CloseButton, $"Close workspace {workspace.Name}");
            UpdateWorkspaceCloseVisibility(workspace);
        }
    }

    private void UpdateWorkspaceCloseVisibility(DesktopWorkspace workspace)
    {
        if (workspace.CloseButton == null) return;
        var visible = workspace == _activeWorkspace || workspace.PointerOver || workspace.FocusWithin;
        // Reserve the slot even when hidden so hovering never shifts tab labels or widths.
        workspace.CloseButton.Opacity = visible ? 1 : 0;
        workspace.CloseButton.IsHitTestVisible = visible;
        workspace.CloseButton.IsTabStop = visible;
    }

    private async Task RenameWorkspaceAsync(DesktopWorkspace workspace)
    {
        var input = new TextBox { Text = workspace.Name, MaxLength = 80, PlaceholderText = "Workspace name" };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Rename workspace",
            Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return;
        workspace.Name = input.Text.Trim();
        RefreshTabStrip();
        SaveWorkspace();
    }

    private async Task CloseWorkspaceAsync(DesktopWorkspace workspace)
    {
        if (_closingWorkspace || !_workspaces.Contains(workspace)) return;
        var reason = _workspaces.Count == 1 ? "Keep at least one workspace open."
            : workspace.Tabs.Any(t => t.View.IsApprovalOpen || t.View.Session.Controller.IsProcessing || t.View.Session.CommandLog.IsRunning)
                ? "Stop running agents and commands, and resolve pending approvals before closing this workspace." : null;
        if (reason != null)
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Workspace is still open",
                Content = reason, CloseButtonText = "OK" }.ShowAsync();
            return;
        }
        _closingWorkspace = true;
        try
        {
            // Archive before touching views. A failure leaves all sessions available.
            foreach (var tab in workspace.Tabs) ArchiveClosedSession(tab.View.Session);
            if (!_archive.TryPersist())
                throw new IOException("The conversation archive could not be saved.");
            var replacement = _activeWorkspace == workspace
                ? _workspaces.First(w => w != workspace) : _activeWorkspace;
            foreach (var tab in workspace.Tabs)
            {
                _agentDirectory.RemovePeer(tab.View.Session.PersistKey);
                tab.View.Shutdown();
                TabHost.Children.Remove(tab.View);
                TabStrip.Children.Remove(tab.Header);
                _sessions.CloseSession(tab.View.Session);
            }
            _workspaces.Remove(workspace);
            if (workspace.Header != null) WorkspaceStrip.Children.Remove(workspace.Header);
            SwitchWorkspace(replacement);
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Could not close workspace",
                Content = ex.Message, CloseButtonText = "OK" }.ShowAsync();
        }
        finally { _closingWorkspace = false; SaveWorkspace(); }
    }
}
