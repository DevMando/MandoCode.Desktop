using MandoCode.Desktop.Services;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class PromptFileLookupTests
{
    [Fact]
    public async Task LargeFolderHasNoMatchCap_AndReopeningUsesWarmCache()
    {
        var root = NewFolder();
        try
        {
            for (var i = 0; i < 1500; i++) File.WriteAllText(Path.Combine(root, $"file-{i:D4}.txt"), "");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "deep-target.txt"), "");
            using var changed = new SemaphoreSlim(0);
            using var provider = new FileAutocompleteProvider(new(root), []);
            var lookup = new PromptFileLookup(provider);
            provider.SuggestionsChanged += () => changed.Release();
            var complete = await Until(lookup, "", changed, result => !result.Loading);
            Assert.Equal(1501, complete.Items.Length);
            Assert.Contains(complete.Items, row => row.Command == "nested/");
            var reopened = await lookup.GetAsync("");
            Assert.False(reopened.Loading);
            Assert.Same(complete.Items, reopened.Items);
            Assert.Equal(complete.Items.Select(row => row.Command), reopened.Items.Select(row => row.Command));
            var deep = await Until(lookup, "target", changed, result => !result.Loading);
            Assert.Equal("nested/deep-target.txt", Assert.Single(deep.Items).Command);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WatcherUpdatesCachedFilesWithoutStartingAnotherRecursiveIndex()
    {
        var root = NewFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "child"));
            using var changed = new SemaphoreSlim(0);
            using var provider = new FileAutocompleteProvider(new(root), []);
            var lookup = new PromptFileLookup(provider);
            provider.SuggestionsChanged += () => changed.Release();
            await Until(lookup, "", changed, result => !result.Loading);
            File.WriteAllText(Path.Combine(root, "child", "new-file.txt"), "new");
            var created = await Until(lookup, "child/", changed, result => !result.Loading && result.Items.Any(row => row.Command == "child/new-file.txt"));
            Assert.False(created.Indexing);
            File.Move(Path.Combine(root, "child", "new-file.txt"), Path.Combine(root, "child", "renamed.txt"));
            var renamed = await Until(lookup, "child/", changed, result => !result.Loading && result.Items.Any(row => row.Command == "child/renamed.txt"));
            Assert.DoesNotContain(renamed.Items, row => row.Command == "child/new-file.txt");
            File.Delete(Path.Combine(root, "child", "renamed.txt"));
            await Until(lookup, "child/", changed, result => !result.Loading && result.Items.Length == 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellingAUiLookup_DoesNotDisposeOrCancelTheSharedIndex()
    {
        var root = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(root, "ready.txt"), "");
            using var changed = new SemaphoreSlim(0);
            using var provider = new FileAutocompleteProvider(new(root), []);
            var lookup = new PromptFileLookup(provider);
            provider.SuggestionsChanged += () => changed.Release();
            await lookup.GetAsync("");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.GetAsync("ready", cancellation.Token));
            var ready = await Until(lookup, "", changed, result => !result.Loading);
            Assert.Equal("ready.txt", Assert.Single(ready.Items).Command);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "desktop-mentions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static async Task<PromptFileSnapshot> Until(PromptFileLookup lookup, string query, SemaphoreSlim changed, Func<PromptFileSnapshot, bool> accept)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = await lookup.GetAsync(query, deadline.Token);
            if (accept(result)) return result;
            await changed.WaitAsync(deadline.Token);
        }
    }
}
