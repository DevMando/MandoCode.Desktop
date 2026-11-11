using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MandoCode.Desktop;

public sealed partial class OllamaModelBrowserWindow : Window
{
    private readonly ChatController _origin;
    private readonly Func<bool> _alive;
    private readonly OllamaBrowserClient _client;
    private readonly OllamaBrowserViewModel _vm;
    private bool _ready, _closed, _rendering, _switching;
    private int _queued;
    private string _actionStatus = "";
    private string? _displayedTag;
    private IReadOnlyList<BrowserModelRow> _models = Array.Empty<BrowserModelRow>();
    private IReadOnlyList<BrowserTagRow> _tags = Array.Empty<BrowserTagRow>();
    public OllamaModelBrowserWindow(ChatController origin, Func<bool> alive, string agentName)
    {
        _origin = origin; _alive = alive;
        _client = new(origin.Config.OllamaEndpoint);
        _vm = new(_client);
        InitializeComponent();
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "images", "mandocode-desktop.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1080, 820));
        Server.Text = "Agent: " + agentName + " · Ollama server: " + _client.Endpoint;
        Root.Loaded += (_, _) => {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Min(1080 * scale, area.Width * 0.9),
                (int)Math.Min(820 * scale, area.Height * 0.9)));
        };
        _vm.Changed += QueueRender;
        _origin.StateChanged += QueueRender;
        ThemeManager.ThemeChanged += ApplyTheme;
        Closed += (_, _) => {
            _closed = true; _ready = false;
            _origin.StateChanged -= QueueRender;
            ThemeManager.ThemeChanged -= ApplyTheme;
            _vm.Changed -= QueueRender;
            _vm.Dispose(); _client.Dispose();
        };
        _ready = true;
        ApplyTheme(); Render();
        _ = _vm.RefreshAsync();
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Root.Width = Math.Max(720, Viewport.ActualWidth);
        Root.Height = Math.Max(700, Viewport.ActualHeight);
    }
    private void ApplyTheme()
    {
        var target = ThemeManager.Current.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = target == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        Root.RequestedTheme = target;
    }
    private void QueueRender()
    {
        if (_closed || Interlocked.Exchange(ref _queued, 1) != 0) return;
        DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _queued, 0); if (!_closed) Render(); });
    }
    private void Render()
    {
        if (!_ready) return;
        _rendering = true;
        try {
            var models = _vm.Models.Select(m => new BrowserModelRow(m)).ToArray();
            if (!_models.Select(m => (m.Name, m.Capabilities, m.Status)).SequenceEqual(models.Select(m => (m.Name, m.Capabilities, m.Status)))) {
                _models = models; Models.ItemsSource = models;
                Models.SelectedItem = models.FirstOrDefault(m => m.Name == _vm.SelectedModel?.Name);
            }
            var tags = _vm.Tags.Select(t => new BrowserTagRow(t)).ToArray();
            if (!_tags.Select(t => (t.Name, t.Size, t.Status)).SequenceEqual(tags.Select(t => (t.Name, t.Size, t.Status)))) {
                _tags = tags; Tags.ItemsSource = tags;
                Tags.SelectedItem = tags.FirstOrDefault(t => t.Name == _vm.SelectedTag);
            }
            if (_displayedTag != _vm.SelectedTag)
            {
                _displayedTag = _vm.SelectedTag;
                Target.Text = _displayedTag ?? "";
            }
            Models.SelectedItem = _models.FirstOrDefault(m => m.Name == _vm.SelectedModel?.Name);
            Tags.SelectedItem = _tags.FirstOrDefault(t => t.Name == _vm.SelectedTag);
            Description.Text = _vm.SelectedModel?.Description ?? "Select a model to browse its tags, or enter an exact tag below.";
            TagStatus.Text = _vm.TagsLoading ? "Loading tags…" : _vm.TagsError;
            Errors.Text = string.Join("\n", new[] { _vm.CatalogLoading ? "Loading catalog…" : _vm.CatalogError, _vm.InventoryLoading ? "Checking installed models…" : _vm.InventoryError }.Where(s => s.Length > 0));
            Status.Text = string.Join("\n", new[] { _vm.DownloadStatus, _vm.ProgressDetail, _vm.DownloadError, _actionStatus }.Where(s => s.Length > 0));
            Progress.Visibility = _vm.Downloading ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = !_vm.ProgressPercent.HasValue;
            Progress.Value = _vm.ProgressPercent ?? 0;
            Cancel.IsEnabled = _vm.CanCancel;
            var target = Target.Text.Trim();
            Pull.IsEnabled = !_vm.Downloading && OllamaBrowserViewModel.ValidTag(target);
            Pull.Content = _vm.IsInstalled(target) ? "Pull again" : "Pull";
            var sameServer = string.Equals(_origin.Config.OllamaEndpoint.TrimEnd('/'), _client.Endpoint, StringComparison.OrdinalIgnoreCase);
            Use.IsEnabled = !_switching && !_vm.Downloading && !_vm.InventoryLoading && _alive() && sameServer && !_origin.IsProcessing && _vm.IsInstalled(target);
            UseStatus.Text = !sameServer ? "This agent changed servers. Reopen the browser to use models from its new server." :
                _origin.IsProcessing ? "Wait for this agent to finish before switching its model." :
                !_vm.IsInstalled(target) ? "Use becomes available when this tag is confirmed on the server." : "";
        } finally { _rendering = false; }
    }
    private void FilterChanged(object sender, TextChangedEventArgs e) { if (!_ready) return; _vm.Query = Search.Text; Render(); }
    private void CloudChanged(object sender, RoutedEventArgs e) { if (!_ready) return; Target.Text = ""; _vm.CloudOnly = Cloud.IsChecked == true; Render(); }
    private async void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Target.Text = "";
        await _vm.LoadCatalogAsync((OllamaCatalogSort)Sort.SelectedIndex);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await _vm.RefreshAsync();
    private async void ModelChanged(object sender, SelectionChangedEventArgs e) { if (_rendering || !_ready) return; if (Models.SelectedItem is BrowserModelRow m) { Target.Text = ""; await _vm.SelectModelAsync(m.Name); } }
    private void TagChanged(object sender, SelectionChangedEventArgs e) { if (_rendering || !_ready) return; if (Tags.SelectedItem is BrowserTagRow t) { Target.Text = t.Name; _vm.SelectTag(t.Name); } }
    private void TargetChanged(object sender, TextChangedEventArgs e) { if (_ready && !_rendering) { _actionStatus = ""; Render(); } }
    private async void PullClicked(object sender, RoutedEventArgs e) => await _vm.PullAsync(Target.Text);
    private void CancelClicked(object sender, RoutedEventArgs e) => _vm.CancelPull();
    private async void UseClicked(object sender, RoutedEventArgs e)
    {
        if (!Use.IsEnabled) return;
        _switching = true; Render();
        try {
            var target = Target.Text.Trim();
            // Refresh immediately before use so a deleted model is not selected from a stale badge.
            await _vm.RefreshInventoryAsync();
            if (!_closed && _alive() && !_vm.InventoryLoading && _vm.IsInstalled(target))
            {
                var selected = await Task.Run(() => _origin.UseBrowserModelAsync(target, _client.Endpoint));
                _actionStatus = selected ? "Selected " + target + " for this agent." : "This agent is busy or changed servers. Try again when it is idle.";
            }
        }
        catch (Exception ex) { if (!_closed) _actionStatus = "Could not switch model: " + ex.Message; }
        finally { _switching = false; if (!_closed) Render(); }
    }
}

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class BrowserModelRow
{
    public BrowserModelRow(OllamaBrowserModel model) { Name = model.Name; Capabilities = model.Capabilities; Status = model.Status; }
    public string Name { get; }
    public string Capabilities { get; }
    public string Status { get; }
}
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class BrowserTagRow
{
    public BrowserTagRow(OllamaBrowserTag tag) { Name = tag.Name; Size = tag.Size; Status = tag.Status; }
    public string Name { get; }
    public string Size { get; }
    public string Status { get; }
}
