using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using MandoCode.Services;
using Xunit;
using System.Net;
using System.Text;

namespace MandoCode.Desktop.Tests;

public class OllamaBrowserTests
{
    [Fact] public void InventoryUsesExactTagsAndPreservesNamespaces()
    {
        var inventory = new OllamaModelInventory(new[] { "library/qwen3", "qwen3:8b", "team/qwen3:4b", "registry:5000/qwen3:8b" });
        Assert.True(inventory.Contains("qwen3:latest"));
        Assert.False(inventory.Contains("qwen3:4b"));
        Assert.False(inventory.Contains("team/qwen3:8b"));
        Assert.True(inventory.Contains("registry:5000/qwen3:8b"));
        Assert.Equal(2, inventory.CountFamily("qwen3"));
    }
    [Fact] public async Task InventoryFailureRemainsUnknownWhileCatalogWorks()
    {
        var fake = new Fake { Inventory = _ => throw new IOException("offline") };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync();
        await vm.SelectModelAsync("qwen3");
        Assert.Single(vm.Models);
        Assert.False(vm.InventoryKnown);
        Assert.All(vm.Tags, t => Assert.Null(t.Installed));
        Assert.Contains("offline", vm.InventoryError);
    }
    [Fact] public async Task BadgeMatchesOnlyInstalledVariant()
    {
        using var vm = new OllamaBrowserViewModel(new Fake());
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.True(vm.Tags[0].Installed);
        Assert.False(vm.Tags[1].Installed);
    }
    [Fact] public async Task LateTagsDoNotReplaceNewSelection()
    {
        var old = new TaskCompletionSource<IReadOnlyList<OllamaModelLibrary.Tag>>();
        var fake = new Fake {
            Catalog = new[] { new OllamaModelLibrary.Model("old", "", ""), new OllamaModelLibrary.Model("new", "", "") },
            Tags = (name, _) => name == "old" ? old.Task : Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] { new OllamaModelLibrary.Tag("new:latest", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync(); var stale = vm.SelectModelAsync("old"); await vm.SelectModelAsync("new");
        old.SetResult(new[] { new OllamaModelLibrary.Tag("old:latest", "") }); await stale;
        Assert.Equal("new:latest", Assert.Single(vm.Tags).Name);
    }
    [Fact] public async Task PullRefreshesInventoryWithoutSelectingModel()
    {
        var installed = new List<string>();
        var fake = new Fake {
            Inventory = _ => Task.FromResult<IReadOnlyList<string>>(installed.ToArray()),
            Pull = (name, progress, _) => { progress(new("pulling", 5, 10)); installed.Add(name); return Task.CompletedTask; }
        };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.PullAsync("qwen3:4b");
        Assert.True(vm.IsInstalled("qwen3:4b"));
        Assert.Null(vm.SelectedModel); Assert.False(vm.Downloading);
    }
    [Fact] public async Task FailedPullDoesNotMarkInstalled()
    {
        using var vm = new OllamaBrowserViewModel(new Fake { Pull = (_, _, _) => throw new IOException("missing") });
        await vm.PullAsync("missing:latest");
        Assert.False(vm.IsInstalled("missing:latest"));
        Assert.Contains("missing", vm.DownloadError);
        Assert.False(vm.Downloading);
    }
    [Fact] public async Task CancellationRefreshesServerInventory()
    {
        var started = new TaskCompletionSource();
        var fake = new Fake { Pull = async (_, _, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        using var vm = new OllamaBrowserViewModel(fake);
        var pull = vm.PullAsync("qwen3:4b"); await started.Task; vm.CancelPull(); await pull;
        Assert.False(vm.Downloading); Assert.True(vm.InventoryKnown);
        Assert.Contains("cancelled", vm.DownloadStatus);
    }
    [Fact] public async Task DisposeCancelsPendingRequests()
    {
        var started = new TaskCompletionSource();
        var fake = new Fake { Pull = async (_, _, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var vm = new OllamaBrowserViewModel(fake); var task = vm.PullAsync("qwen3:4b");
        await started.Task; vm.Dispose(); await task;
        Assert.False(vm.Downloading);
    }
    [Theory]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"models\":[{\"name\":\"qwen3:8b\"}]}")]
    public async Task InventoryQueriesCapturedServer(string json)
    {
        using var client = new OllamaBrowserClient("http://localhost:1234", new HttpClient(new Handler(req => {
            Assert.Equal("http://localhost:1234/api/tags", req.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        })));
        var result = await client.InstalledAsync(default);
        Assert.Equal(json.Contains("qwen3") ? 1 : 0, result.Count);
    }
    [Fact] public async Task MalformedInventoryDoesNotMeanEmpty()
    {
        using var client = new OllamaBrowserClient("http://localhost", new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") })));
        await Assert.ThrowsAsync<IOException>(() => client.InstalledAsync(default));
    }
    [Fact] public async Task CloudTagsAreAvailabilityRatherThanLocalDownloadBadges()
    {
        var fake = new Fake {
            Inventory = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3:cloud" }),
            Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] { new OllamaModelLibrary.Tag("qwen3:cloud", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.Equal("✓ Available on server", Assert.Single(vm.Tags).Status);
    }

    [Fact] public async Task PullSendsCapturedEndpointAndReportsStreamedProgress()
    {
        var stages = new List<OllamaModelLibrary.Progress>();
        using var client = new OllamaBrowserClient("http://localhost:1234", new HttpClient(new Handler(req => {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("http://localhost:1234/api/pull", req.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) {
                Content = new StringContent("{\"status\":\"pulling\",\"completed\":5,\"total\":10}\n{\"status\":\"success\"}\n")
            };
        })));
        await client.PullAsync("qwen3:8b", stages.Add, default);
        Assert.Contains(stages, p => p.Completed == 5 && p.Total == 10);
    }

    [Fact] public async Task SelectingFamilyChoosesLatestTagWithoutAnotherClick()
    {
        var fake = new Fake {
            Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] {
                new OllamaModelLibrary.Tag("qwen3:4b", ""),
                new OllamaModelLibrary.Tag("qwen3:latest", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.Equal("qwen3:latest", vm.SelectedTag);
    }

    [Fact] public async Task CloudOnlySelectionChoosesCloudInsteadOfLocalLatest()
    {
        var fake = new Fake {
            Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] {
                new OllamaModelLibrary.Tag("qwen3:latest", ""),
                new OllamaModelLibrary.Tag("qwen3:cloud", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake) { CloudOnly = true };
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.Equal("qwen3:cloud", vm.SelectedTag);
    }

    [Fact] public async Task CloudFamilyWithOneTagDoesNotRequireTagSelection()
    {
        var fake = new Fake {
            Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] { new OllamaModelLibrary.Tag("qwen3:cloud", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.Equal("qwen3:cloud", vm.SelectedTag);
    }

    [Fact] public async Task ChangingSortClearsSelectionAndRejectsLateTags()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<OllamaModelLibrary.Tag>>();
        var fake = new Fake { Tags = (_, _) => pending.Task };
        using var vm = new OllamaBrowserViewModel(fake);
        await vm.RefreshAsync();
        var lookup = vm.SelectModelAsync("qwen3");
        await vm.LoadCatalogAsync(OllamaCatalogSort.Popular);
        Assert.Null(vm.SelectedModel); Assert.Null(vm.SelectedTag);
        Assert.Empty(vm.Tags); Assert.False(vm.TagsLoading);
        pending.SetResult(new[] { new OllamaModelLibrary.Tag("qwen3:latest", "") });
        await lookup;
        Assert.Empty(vm.Tags); Assert.Null(vm.SelectedTag);
    }

    [Fact] public async Task ChangingSortClearsPreviouslyLoadedDetails()
    {
        using var vm = new OllamaBrowserViewModel(new Fake());
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.NotNull(vm.SelectedTag);
        await vm.LoadCatalogAsync(OllamaCatalogSort.Name);
        Assert.Empty(vm.Tags); Assert.Null(vm.SelectedModel); Assert.Null(vm.SelectedTag);
    }

    [Fact] public async Task CloudOnlyHidesLocalVariantsAndCountsOnlyCloudInventory()
    {
        var fake = new Fake {
            Catalog = new[] { new OllamaModelLibrary.Model("mixed", "", "tools · cloud"), new OllamaModelLibrary.Model("local", "", "tools") },
            Inventory = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "mixed:latest", "mixed:small", "mixed:cloud" }),
            Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] {
                new OllamaModelLibrary.Tag("mixed:latest", "5GB"),
                new OllamaModelLibrary.Tag("mixed:cloud", ""),
                new OllamaModelLibrary.Tag("mixed:large-cloud", "") })
        };
        using var vm = new OllamaBrowserViewModel(fake) { CloudOnly = true };
        await vm.RefreshAsync(); await vm.SelectModelAsync("mixed");
        Assert.Equal("mixed", Assert.Single(vm.Models).Name);
        Assert.Contains("1 tag(s)", vm.Models[0].Status);
        Assert.Equal(2, vm.Tags.Count);
        Assert.All(vm.Tags, t => Assert.True(MandoCode.Models.MandoCodeConfig.IsCloudModel(t.Name)));
        Assert.Equal("mixed:cloud", vm.SelectedTag);
    }

    [Fact] public async Task CloudOnlyNeverFallsBackToLocalLatest()
    {
        using var vm = new OllamaBrowserViewModel(new Fake()) { CloudOnly = true };
        await vm.RefreshAsync(); await vm.SelectModelAsync("qwen3");
        Assert.Null(vm.SelectedTag); Assert.Empty(vm.Tags);
        Assert.Contains("No cloud tags", vm.TagsError);
    }

    [Fact] public async Task CloudFilterChangeRejectsPendingLocalTags()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<OllamaModelLibrary.Tag>>();
        using var vm = new OllamaBrowserViewModel(new Fake { Tags = (_, _) => pending.Task });
        await vm.RefreshAsync(); var lookup = vm.SelectModelAsync("qwen3");
        vm.CloudOnly = true;
        pending.SetResult(new[] { new OllamaModelLibrary.Tag("qwen3:latest", "") });
        await lookup;
        Assert.Null(vm.SelectedModel); Assert.Null(vm.SelectedTag); Assert.Empty(vm.Tags);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }
    private sealed class Fake : IOllamaBrowserClient
    {
        public IReadOnlyList<OllamaModelLibrary.Model> Catalog = new[] { new OllamaModelLibrary.Model("qwen3", "test", "tools") };
        public Func<CancellationToken, Task<IReadOnlyList<string>>> Inventory = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3:8b" });
        public Func<string, CancellationToken, Task<IReadOnlyList<OllamaModelLibrary.Tag>>> Tags = (_, _) => Task.FromResult<IReadOnlyList<OllamaModelLibrary.Tag>>(new[] { new OllamaModelLibrary.Tag("qwen3:8b", "5 GB"), new OllamaModelLibrary.Tag("qwen3:4b", "3 GB") });
        public Func<string, Action<OllamaModelLibrary.Progress>, CancellationToken, Task> Pull = (_, _, _) => Task.CompletedTask;
        public Task<IReadOnlyList<OllamaModelLibrary.Model>> ModelsAsync(bool newest, CancellationToken ct) => Task.FromResult(Catalog);
        public Task<IReadOnlyList<OllamaModelLibrary.Tag>> TagsAsync(string name, CancellationToken ct) => Tags(name, ct);
        public Task<IReadOnlyList<string>> InstalledAsync(CancellationToken ct) => Inventory(ct);
        public Task PullAsync(string name, Action<OllamaModelLibrary.Progress> progress, CancellationToken ct) => Pull(name, progress, ct);
    }
}
