using System.Text.Json;
using MandoCode.Services;

namespace MandoCode.Desktop.Services;

public interface IOllamaBrowserClient
{
    Task<IReadOnlyList<OllamaModelLibrary.Model>> ModelsAsync(bool newest, CancellationToken ct);
    Task<IReadOnlyList<OllamaModelLibrary.Tag>> TagsAsync(string model, CancellationToken ct);
    Task<IReadOnlyList<string>> InstalledAsync(CancellationToken ct);
    Task PullAsync(string name, Action<OllamaModelLibrary.Progress> progress, CancellationToken ct);
}

/// <summary>Shares CLI catalog/pull behavior; inventory always comes from the captured server.</summary>
public sealed class OllamaBrowserClient : IOllamaBrowserClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaModelLibrary _library;
    public string Endpoint { get; }

    public OllamaBrowserClient(string endpoint, HttpClient? http = null)
    {
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Use an HTTP or HTTPS Ollama endpoint.");
        Endpoint = endpoint.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _library = new(_http);
    }

    public Task<IReadOnlyList<OllamaModelLibrary.Model>> ModelsAsync(bool newest, CancellationToken ct) => _library.Models(ct, newest);
    public Task<IReadOnlyList<OllamaModelLibrary.Tag>> TagsAsync(string model, CancellationToken ct) => _library.Tags(model, ct);
    public Task PullAsync(string name, Action<OllamaModelLibrary.Progress> progress, CancellationToken ct) =>
        _library.Pull(Endpoint, name, progress, ct);

    public async Task<IReadOnlyList<string>> InstalledAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await _http.GetAsync(Endpoint + "/api/tags", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new IOException("The server did not return a model inventory.");
        var names = new List<string>();
        foreach (var model in models.EnumerateArray())
        {
            var name = model.TryGetProperty("name", out var value) ? value.GetString()
                : model.TryGetProperty("model", out value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                throw new IOException("The server returned an invalid model entry.");
            names.Add(name);
        }
        return names;
    }

    public void Dispose() => _http.Dispose();
}
