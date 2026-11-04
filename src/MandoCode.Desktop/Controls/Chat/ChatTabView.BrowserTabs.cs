using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using MandoCode.Desktop.Services;
using Windows.System;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    // Each operation receives this object explicitly. UI selection never participates in routing.
    private sealed class BrowserTab
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public WebView2 View { get; } = new();
        public CancellationTokenSource Lifetime { get; } = new();
        public required string ProjectRoot { get; init; }
        public bool External { get; set; }
        public bool Closed { get; set; }
        public string? FilePath { get; set; }
        public string? Origin { get; set; }
        public int AgentRequests;
        public long DocumentVersion;
        public ulong NavigationId;
        public bool CacheBypassed;
        public TaskCompletionSource<string?>? Navigation;
        public BrowserFrames? Frames;
        public List<CoreWebView2DevToolsProtocolEventReceiver> EventReceivers { get; } = [];
        public Queue<object> Diagnostics { get; } = new();
        public Dictionary<string, bool> DiagnosticDomains { get; } = new();
        public StackPanel Header { get; } = new() { Orientation = Orientation.Horizontal };
        public Button SelectButton { get; } = new() { MaxWidth = 180, Padding = new Thickness(8, 5, 8, 5) };
    }

    private readonly List<BrowserTab> _browserTabs = [];
    private BrowserTab? _selectedBrowserTab;

    private async Task<BrowserTab> CreateBrowserTabAsync(bool external, CancellationToken token)
    {
        var tab = new BrowserTab { ProjectRoot = _controller.ProjectRootPath, External = external };
        _browserTabs.Add(tab);
        BrowserViews.Children.Add(tab.View);
        tab.SelectButton.Content = "New tab";
        tab.SelectButton.Click += (_, _) => SelectBrowserTab(tab);
        var close = new Button { Content = "×", Padding = new Thickness(5) };
        ToolTipService.SetToolTip(close, "Close browser tab");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close browser tab");
        close.Click += (_, _) => CloseBrowserTab(tab);
        tab.Header.Children.Add(tab.SelectButton);
        tab.Header.Children.Add(close);
        BrowserTabStrip.Children.Add(tab.Header);
        SelectBrowserTab(tab);
        try
        {
            await tab.View.EnsureCoreWebView2Async();
            token.ThrowIfCancellationRequested();
            if (tab.Closed || _shutDown) throw new OperationCanceledException();
            var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("Browser initialization failed.");
            await InitializePreviewAutomationAsync(tab, core);
            core.DocumentTitleChanged += (_, _) => UpdateBrowserChrome(tab);
            core.SourceChanged += (_, _) => UpdateBrowserChrome(tab);
            core.HistoryChanged += (_, _) => UpdateBrowserChrome(tab);
            return tab;
        }
        catch
        {
            CloseBrowserTab(tab);
            throw;
        }
    }

    private void SelectBrowserTab(BrowserTab tab)
    {
        if (tab.Closed) return;
        _selectedBrowserTab = tab;
        foreach (var other in _browserTabs)
        {
            other.View.Visibility = other == tab ? Visibility.Visible : Visibility.Collapsed;
            other.SelectButton.Opacity = other == tab ? 1 : 0.6;
        }
        PreviewText.Visibility = PreviewImageScroll.Visibility = PreviewMessage.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Visible;
        _browserPreview = true;
        _previewPath = tab.FilePath;
        PreviewEditButton.IsEnabled = PreviewSaveButton.IsEnabled = false;
        PreviewReloadButton.Visibility = Visibility.Collapsed;
        TogglePreview(true);
        UpdateBrowserChrome(tab);
    }

    private void UpdateBrowserChrome(BrowserTab tab)
    {
        if (tab.Closed) return;
        var core = tab.View.CoreWebView2;
        tab.SelectButton.Content = string.IsNullOrWhiteSpace(core?.DocumentTitle) ? "New tab" : core.DocumentTitle;
        ToolTipService.SetToolTip(tab.SelectButton, core?.Source ?? "New tab");
        if (tab != _selectedBrowserTab || !_browserPreview) return;
        BrowserAddressBox.Text = core?.Source == "about:blank" ? "" : core?.Source ?? "";
        BrowserBackButton.IsEnabled = core?.CanGoBack == true;
        BrowserForwardButton.IsEnabled = core?.CanGoForward == true;
        _previewTitle = "Browser";
        UpdatePreviewTitle();
    }

    private void CloseBrowserTab(BrowserTab tab)
    {
        if (tab.Closed) return;
        tab.Closed = true;
        tab.Lifetime.Cancel();
        tab.Navigation?.TrySetResult("The targeted browser tab closed. No other tab was used.");
        tab.View.Close();
        BrowserViews.Children.Remove(tab.View);
        BrowserTabStrip.Children.Remove(tab.Header);
        _browserTabs.Remove(tab);
        if (_selectedBrowserTab != tab) return;
        _selectedBrowserTab = null;
        if (!_shutDown && _browserTabs.LastOrDefault() is { } remaining) SelectBrowserTab(remaining);
        else if (!_shutDown) TogglePreview(false);
    }

    private async Task NavigateBrowserTabAsync(BrowserTab tab, string? url, string? filePath, CancellationToken token)
    {
        if (tab.Closed) throw new InvalidOperationException("The targeted browser tab closed.");
        var core = tab.View.CoreWebView2 ?? throw new InvalidOperationException("Browser is not ready.");
        if (filePath != null)
        {
            tab.External = false;
            tab.FilePath = filePath;
            tab.Origin = $"https://{PreviewBrowserHost}";
            core.SetVirtualHostNameToFolderMapping(PreviewBrowserHost, tab.ProjectRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            var relative = Path.GetRelativePath(tab.ProjectRoot, filePath).Replace('\\', '/');
            url = tab.Origin + "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        }
        else
        {
            core.ClearVirtualHostNameToFolderMapping(PreviewBrowserHost);
            tab.FilePath = null;
            tab.Origin = OriginOf(url);
        }
        if (url == null) throw new InvalidOperationException("A URL or project file is required.");
        await NavigatePreviewConfirmedAsync(core, () => { core.Navigate(url); return Task.CompletedTask; }, token);
        if (tab == _selectedBrowserTab)
        {
            _previewPath = tab.FilePath;
            if (tab.FilePath != null) CapturePreviewFileStamp(tab.FilePath);
        }
        UpdateBrowserChrome(tab);
    }

    private async Task OpenUserBrowserUrlAsync(string? url = null)
    {
        try
        {
            if (_previewDirty && !await ConfirmDiscardPreviewChangesAsync()) return;
            ResetPreviewEditing();
            var tab = await CreateBrowserTabAsync(true, _previewAutomationLifetime.Token);
            if (url != null) await NavigateBrowserTabAsync(tab, url, null, tab.Lifetime.Token);
            else BrowserAddressBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) { _transcript.Append(_html.Warn("Couldn't open browser: " + ex.Message)); }
    }

    private async void BrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewDirty && !await ConfirmDiscardPreviewChangesAsync()) return;
        ResetPreviewEditing();
        if (_selectedBrowserTab is { Closed: false } tab) SelectBrowserTab(tab);
        else await OpenUserBrowserUrlAsync();
    }

    private async void BrowserNewTab_Click(object sender, RoutedEventArgs e) => await OpenUserBrowserUrlAsync();
    private void BrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserTab?.View.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
    }
    private void BrowserForward_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserTab?.View.CoreWebView2 is { CanGoForward: true } core) core.GoForward();
    }

    private async void BrowserAddress_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _selectedBrowserTab is not { Closed: false } tab) return;
        e.Handled = true;
        var address = BrowserAddressBox.Text.Trim();
        if (!address.Contains("://")) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            _transcript.Append(_html.Warn("Enter an HTTP or HTTPS URL without embedded credentials."));
            return;
        }
        try
        {
            tab.External = true;
            await NavigateBrowserTabAsync(tab, uri.AbsoluteUri, null, tab.Lifetime.Token);
        }
        catch (Exception ex) { _transcript.Append(_html.Warn("Browser navigation failed: " + ex.Message)); }
    }

    private string? CaptureBrowserRequestContext() =>
        _previewOpen && _browserPreview && _selectedBrowserTab is { Closed: false } tab
            ? BrowserRequestContext.Capture(tab.Id, tab.View.CoreWebView2?.Source)
            : null;
}
