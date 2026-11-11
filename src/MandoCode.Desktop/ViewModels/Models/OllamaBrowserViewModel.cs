using MandoCode.Services;
using System.Text.RegularExpressions;

using MandoCode.Desktop.Services;
namespace MandoCode.Desktop.ViewModels;

public enum OllamaCatalogSort { Newest, Popular, Name }
public sealed record OllamaBrowserModel(string Name, string Description, string Capabilities, string Status, bool Cloud);
public sealed record OllamaBrowserTag(string Name, string Size, string Status, bool? Installed);

/// <summary>UI-independent browser state. Stale lookups cannot overwrite newer selections.</summary>
public sealed class OllamaBrowserViewModel : IDisposable
{
    private readonly IOllamaBrowserClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _catalogRequest, _tagsRequest, _inventoryRequest, _pullRequest;
    private int _catalogVersion, _tagVersion, _inventoryVersion;
    private bool _disposed;
    private IReadOnlyList<OllamaModelLibrary.Model> _models = Array.Empty<OllamaModelLibrary.Model>();
    private IReadOnlyList<OllamaModelLibrary.Tag> _tags = Array.Empty<OllamaModelLibrary.Tag>();
    private OllamaModelInventory? _inventory;
    public event Action? Changed;
    public OllamaBrowserViewModel(IOllamaBrowserClient client) => _client = client;

    public string Query { get; set; } = "";
    private bool _cloudOnly;
    public bool CloudOnly
    {
        get => _cloudOnly;
        set
        {
            if (_disposed || value == _cloudOnly) return;
            _cloudOnly = value;
            ClearSelection();
            Notify();
        }
    }

    private void ClearSelection()
    {
        ++_tagVersion;
        _tagsRequest?.Cancel();
        _tagsRequest = null;
        SelectedModel = null;
        SelectedTag = null;
        _tags = Array.Empty<OllamaModelLibrary.Tag>();
        TagsLoading = false;
        TagsError = "";
    }
    public OllamaCatalogSort Sort { get; private set; }
    public string CatalogError { get; private set; } = "";
    public string InventoryError { get; private set; } = "";
    public string TagsError { get; private set; } = "";
    public string DownloadStatus { get; private set; } = "";
    public string DownloadError { get; private set; } = "";
    public bool CatalogLoading { get; private set; }
    public bool InventoryLoading { get; private set; }
    public bool TagsLoading { get; private set; }
    public bool Downloading { get; private set; }
    public bool CanCancel => Downloading && _pullRequest?.IsCancellationRequested == false;
    public double? ProgressPercent { get; private set; }
    public string ProgressDetail { get; private set; } = "";
    public string? DownloadTarget { get; private set; }
    public string? LastDownloaded { get; private set; }
    public OllamaModelLibrary.Model? SelectedModel { get; private set; }
    public string? SelectedTag { get; private set; }
    public bool InventoryKnown => _inventory != null;
    public bool IsInstalled(string name) => _inventory?.Contains(name) == true;
    public static bool IsCloud(string capabilities) =>
        capabilities.Split('·', StringSplitOptions.TrimEntries).Contains("cloud", StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OllamaBrowserModel> Models
    {
        get
        {
            var matches = _models.Where(m => m.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)
                && (!CloudOnly || IsCloud(m.Capabilities)));
            if (Sort == OllamaCatalogSort.Name) matches = matches.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
            return matches.Select(m =>
            {
                var count = _inventory?.CountFamily(m.Name, CloudOnly);
                return new OllamaBrowserModel(m.Name, m.Description, m.Capabilities,
                    count == null ? "Status unavailable" : count > 0 ? $"✓ {count} tag(s) available" : "", IsCloud(m.Capabilities));
            }).ToArray();
        }
    }

    public IReadOnlyList<OllamaBrowserTag> Tags => _tags.Where(tag => !CloudOnly || MandoCode.Models.MandoCodeConfig.IsCloudModel(tag.Name)).Select(tag =>
    {
        bool? installed = _inventory?.Contains(tag.Name);
        var cloud = MandoCode.Models.MandoCodeConfig.IsCloudModel(tag.Name);
        return new OllamaBrowserTag(tag.Name, tag.Size,
            installed == null ? "Status unavailable" : installed == true ? cloud ? "✓ Available on server" : "✓ Pulled" : "Not pulled", installed);
    }).ToArray();

    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public Task RefreshAsync() => Task.WhenAll(LoadCatalogAsync(Sort), RefreshInventoryAsync());

    public async Task LoadCatalogAsync(OllamaCatalogSort sort)
    {
        if (_disposed) return;
        Sort = sort;
        ClearSelection();
        var version = ++_catalogVersion;
        _catalogRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _catalogRequest = request;
        CatalogLoading = true; CatalogError = ""; Notify();
        try
        {
            var models = await _client.ModelsAsync(sort == OllamaCatalogSort.Newest, request.Token);
            if (_disposed || version != _catalogVersion) return;
            _models = models;
            if (models.Count == 0) CatalogError = "The public catalog returned no models. Refresh or enter a model tag below.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _catalogVersion) { _models = Array.Empty<OllamaModelLibrary.Model>(); CatalogError = "Catalog unavailable. Refresh or enter a model tag below. " + ex.Message; }
        }
        finally
        {
            if (version == _catalogVersion) { _catalogRequest = null; CatalogLoading = false; Notify(); }
        }
    }

    public async Task RefreshInventoryAsync()
    {
        if (_disposed) return;
        var version = ++_inventoryVersion;
        _inventoryRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _inventoryRequest = request;
        InventoryLoading = true; InventoryError = ""; Notify();
        try
        {
            var names = await _client.InstalledAsync(request.Token);
            if (_disposed || version != _inventoryVersion) return;
            _inventory = new(names);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _inventoryVersion) { _inventory = null; InventoryError = "Installed status unavailable. Check this server and refresh. " + ex.Message; }
        }
        finally
        {
            if (version == _inventoryVersion) { _inventoryRequest = null; InventoryLoading = false; Notify(); }
        }
    }

    public async Task SelectModelAsync(string name)
    {
        if (_disposed) return;
        var model = _models.FirstOrDefault(m => m.Name == name);
        if (model == null) return;
        SelectedModel = model; SelectedTag = null; _tags = Array.Empty<OllamaModelLibrary.Tag>();
        var version = ++_tagVersion;
        _tagsRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tagsRequest = request;
        TagsLoading = true; TagsError = ""; Notify();
        try
        {
            var tags = await _client.TagsAsync(model.Name, request.Token);
            if (_disposed || version != _tagVersion) return;
            _tags = CloudOnly
                ? tags.Where(tag => MandoCode.Models.MandoCodeConfig.IsCloudModel(tag.Name)).ToArray()
                : tags;
            SelectedTag = _tags.FirstOrDefault(tag => tag.Name == model.Name + ":latest")?.Name
                ?? _tags.FirstOrDefault()?.Name;
            if (_tags.Count == 0) TagsError = CloudOnly
                ? "No cloud tags found for this family. Turn off Cloud only to browse local variants."
                : "No tags found. Enter an exact model:tag below.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (version == _tagVersion) TagsError = "Tags unavailable. Enter a model:tag below. " + ex.Message; }
        finally
        {
            if (version == _tagVersion) { _tagsRequest = null; TagsLoading = false; Notify(); }
        }
    }

    public void SelectTag(string? name)
    {
        SelectedTag = name != null && _tags.Any(t => t.Name == name) ? name : null;
        Notify();
    }

    public static bool ValidTag(string name) => name.Length is > 0 and <= 256
        && Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9._:/-]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task PullAsync(string name)
    {
        if (_disposed || Downloading) return;
        name = name.Trim();
        DownloadError = "";
        if (!ValidTag(name)) { DownloadError = "Enter a valid model name or model:tag."; Notify(); return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pullRequest = request; Downloading = true; DownloadTarget = name;
        ProgressPercent = null; ProgressDetail = ""; DownloadStatus = "Starting pull: " + name; Notify();
        try
        {
            await _client.PullAsync(name, progress =>
            {
                if (_disposed || request.IsCancellationRequested) return;
                DownloadStatus = name + ": " + progress.Status;
                ProgressPercent = progress.Total > 0 ? Math.Clamp(progress.Completed * 100d / progress.Total, 0, 100) : null;
                ProgressDetail = progress.Total > 0 ? $"{progress.Completed / 1048576d:F1} / {progress.Total / 1048576d:F1} MiB in this stage" : "";
                Notify();
            }, request.Token);
            request.Token.ThrowIfCancellationRequested();
            LastDownloaded = name;
            DownloadStatus = name + " pulled. Refreshing server inventory…";
            await RefreshInventoryAsync();
            DownloadStatus = name + " pulled. Choose Use in this agent when ready.";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            DownloadStatus = "Pull cancelled. Select the same tag to retry; refresh to check server availability.";
            await RefreshInventoryAsync();
        }
        catch (Exception ex)
        {
            DownloadError = ex.Message;
            DownloadStatus = "Pull did not complete. Refresh server status before retrying.";
        }
        finally { _pullRequest = null; Downloading = false; ProgressPercent = null; Notify(); }
    }

    public void CancelPull()
    {
        if (!CanCancel) return;
        DownloadStatus = "Cancelling pull…";
        _pullRequest?.Cancel(); Notify();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Changed = null;
    }
}
