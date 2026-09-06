using System.Text.Json;
using MandoCode.Desktop.Services;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class DesktopPreviewToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MandoPreviewTests-" + Guid.NewGuid().ToString("N"));
    public DesktopPreviewToolsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private DesktopPreviewTools Tools() => new(new ProjectRootAccessor(_root));
    private static bool Ok(string json) => JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean();

    [Fact]
    public async Task UnattachedPaneCannotClaimSuccess()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<h1>Test</h1>");
        Assert.False(Ok(await Tools().OpenDesktopPreview("index.html")));
        Assert.False(Ok(await Tools().RefreshDesktopPreview()));
    }

    [Fact]
    public async Task OpenWaitsForObservedNavigationResult()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<h1>Test</h1>");
        var tools = Tools();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tools.ExecuteAsync = (request, _) =>
        {
            Assert.Equal("open", request.Operation);
            Assert.Equal(Path.Combine(_root, "index.html"), request.FullPath);
            return pending.Task;
        };
        var result = tools.OpenDesktopPreview("index.html");
        Assert.False(result.IsCompleted);
        pending.SetResult("{\"ok\":false,\"error\":\"navigation failed\"}");
        Assert.False(Ok(await result));
    }

    [Theory]
    [InlineData("../outside.html")]
    [InlineData("C:/outside.html")]
    [InlineData("index.html:stream.html")]
    [InlineData("")]
    [InlineData("missing.html")]
    [InlineData("readme.txt")]
    [InlineData("bad\0.html")]
    public async Task InvalidPathsNeverReachBrowser(string path)
    {
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "text");
        var tools = Tools();
        tools.ExecuteAsync = (_, _) => throw new Exception("Must not dispatch");
        var result = await tools.OpenDesktopPreview(path);
        Assert.False(Ok(result));
        Assert.DoesNotContain("Must not dispatch", result);
    }

    [Fact]
    public async Task ActionsAreSerialized_AndTimeoutDoesNotRepeatOrOverlap()
    {
        var tools = Tools();
        tools.OperationTimeout = TimeSpan.FromMilliseconds(100);
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        tools.ExecuteAsync = (_, _) => { calls++; return pending.Task; };
        Assert.Contains("timed out", await tools.ClickDesktopPreview("#save"));
        Assert.Contains("timed out", await tools.ClickDesktopPreview("#save"));
        Assert.Equal(1, calls);
        pending.SetResult("{\"ok\":true}");
    }

    [Fact]
    public async Task CancellationIsPassedThroughToHost()
    {
        var tools = Tools();
        using var cancellation = new CancellationTokenSource();
        tools.ExecuteAsync = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "{}"; };
        var result = tools.InspectDesktopPreview(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
    }

    [Fact]
    public async Task SelectorsAndValuesArePassedAsData()
    {
        var tools = Tools();
        tools.ExecuteAsync = (request, _) =>
        {
            Assert.Equal("fill", request.Operation);
            Assert.Equal("#name", request.Selector);
            Assert.Equal("');window.pwned=true;//", request.Value);
            Assert.Equal(_root, request.ProjectRoot);
            return Task.FromResult("{\"ok\":true}");
        };
        Assert.True(Ok(await tools.FillDesktopPreview("#name", "');window.pwned=true;//")));
    }

    [Fact]
    public async Task InvalidArgumentsNeverDispatch()
    {
        var tools = Tools();
        var calls = 0;
        tools.ExecuteAsync = (_, _) => { calls++; return Task.FromResult("{}"); };
        Assert.False(Ok(await tools.ClickDesktopPreview("")));
        Assert.False(Ok(await tools.InspectDesktopPreview(offset: -1)));
        Assert.False(Ok(await tools.FillDesktopPreview("#name", new string('x', 10001))));
        Assert.False(Ok(await tools.ObserveDesktopPreview(" ")));
        Assert.False(Ok(await tools.ClickDesktopPreview("#save", count: 0)));
        Assert.False(Ok(await tools.ClickDesktopPreview("#save", count: DesktopPreviewTools.MaxRepeats + 1)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UnknownKeysAndModifiersNeverDispatch()
    {
        var tools = Tools();
        var calls = 0;
        tools.ExecuteAsync = (_, _) => { calls++; return Task.FromResult("{\"ok\":true}"); };
        Assert.False(Ok(await tools.PressKeyDesktopPreview("Ctrl+Enter")));       // a chord is not a key name
        Assert.False(Ok(await tools.PressKeyDesktopPreview("Enter", modifiers: "hyper")));
        Assert.False(Ok(await tools.PressKeyDesktopPreview("")));
        Assert.False(Ok(await tools.PressKeyDesktopPreview("Enter", count: DesktopPreviewTools.MaxRepeats + 1)));
        Assert.False(Ok(await tools.PressKeyDesktopPreview("Enter", holdMs: DesktopPreviewTools.MaxKeyHoldMs + 1)));
        Assert.False(Ok(await tools.PressKeyDesktopPreview("ArrowUp", count: 25, holdMs: 1000)));   // 25s of holding
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ResolvedKeysReachTheHostAsData()
    {
        var tools = Tools();
        DesktopPreviewRequest? seen = null;
        tools.ExecuteAsync = (request, _) => { seen = request; return Task.FromResult("{\"ok\":true}"); };

        Assert.True(Ok(await tools.PressKeyDesktopPreview("Enter", selector: "#search", modifiers: "ctrl+shift")));
        Assert.Equal("key", seen!.Operation);
        Assert.Equal("#search", seen.Selector);
        Assert.Equal("Enter", seen.Key!.Key);
        Assert.Equal(DesktopPreviewKeys.Control | DesktopPreviewKeys.Shift, seen.Modifiers);

        Assert.True(Ok(await tools.PressKeyDesktopPreview("A")));
        Assert.Equal("KeyA", seen!.Key!.Code);
        Assert.Equal(DesktopPreviewKeys.Shift, seen.Modifiers);   // a capital letter is a shifted key
    }

    [Fact]
    public async Task InterruptedRepeatsReportProgressInsteadOfReplaying()
    {
        var tools = Tools();
        tools.OperationTimeout = TimeSpan.FromMilliseconds(150);
        var calls = 0;
        tools.ExecuteAsync = async (request, token) =>
        {
            calls++;
            request.Progress!.Note(3);   // the host got three clicks out before the deadline
            await Task.Delay(Timeout.Infinite, token);
            return "{}";
        };
        var result = await tools.ClickDesktopPreview("#increment", count: 10);
        Assert.False(Ok(result));
        Assert.Contains("3 of 10 repeats completed", result);
        Assert.Contains("not replayed", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FocusedObservationsRideAlongWithActions()
    {
        var tools = Tools();
        DesktopPreviewRequest? seen = null;
        tools.ExecuteAsync = (request, _) => { seen = request; return Task.FromResult("{\"ok\":true}"); };
        Assert.True(Ok(await tools.ClickDesktopPreview("#increment", observe: "#count")));
        Assert.Equal("#count", seen!.Observe);
        Assert.True(Ok(await tools.ObserveDesktopPreview("#count")));
        Assert.Equal("observe", seen!.Operation);
        Assert.Equal("#count", seen.Observe);
        Assert.Null(seen.Selector);
    }

    [Theory]
    [InlineData("Enter", "\\r", "keyDown")]          // a text key inserts its character
    [InlineData("ArrowUp", "\"text\":\"\"", "rawKeyDown")]  // a navigation key must not insert one
    public void KeyEventsCarryTheBrowserFields(string name, string expectedText, string expectedType)
    {
        Assert.True(DesktopPreviewKeys.TryResolve(name, out var key, out _, out _));
        var payload = DesktopPreviewKeys.BuildKeyEvent(key, 0, down: true);
        Assert.Contains(expectedText, payload);
        Assert.Contains($"\"type\":\"{expectedType}\"", payload);
        Assert.Contains("\"type\":\"keyUp\"", DesktopPreviewKeys.BuildKeyEvent(key, 0, down: false));
    }

    [Fact]
    public void ModifierChordsDoNotAlsoTypeTheCharacter()
    {
        Assert.True(DesktopPreviewKeys.TryResolve("a", out var key, out _, out _));
        Assert.Contains("\"text\":\"a\"", DesktopPreviewKeys.BuildKeyEvent(key, DesktopPreviewKeys.Shift, down: true));
        Assert.Contains("\"text\":\"\"", DesktopPreviewKeys.BuildKeyEvent(key, DesktopPreviewKeys.Control, down: true));
    }
}
