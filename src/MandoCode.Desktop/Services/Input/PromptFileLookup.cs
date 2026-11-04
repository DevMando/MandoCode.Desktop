using MandoCode.Services;

namespace MandoCode.Desktop.Services;

public sealed record PromptFileSnapshot(CommandSuggestion[] Items, bool Loading, bool Indexing);

/// <summary>All potentially large result copies/projections stay off the UI thread.
/// Cancelling a caller drops its results, while the shared provider keeps warming its index.</summary>
public sealed class PromptFileLookup(FileAutocompleteProvider provider)
{
    private readonly object _gate = new();
    private List<string>? _lastPaths;
    private CommandSuggestion[]? _lastItems;
    public Task<PromptFileSnapshot> GetAsync(string query, CancellationToken cancellation = default)
        => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var paths = provider.GetSuggestions(query);
            lock (_gate)
            {
                // Index completion often leaves the root listing unchanged. Reuse the
                // projection so the native list keeps its viewport and item identity.
                if (_lastPaths is not null && paths.SequenceEqual(_lastPaths))
                    return new PromptFileSnapshot(_lastItems!, provider.IsLoading, provider.IsIndexing);
                var items = new CommandSuggestion[paths.Count];
                for (var i = 0; i < paths.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = paths[i];
                    items[i] = new() { Command = path, Description = path.EndsWith('/') ? "folder — select to drill in" : "file" };
                }
                _lastPaths = paths; _lastItems = items;
                return new PromptFileSnapshot(items, provider.IsLoading, provider.IsIndexing);
            }
        }, cancellation);
}
