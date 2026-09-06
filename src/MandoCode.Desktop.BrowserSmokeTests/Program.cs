using System.Text.Json;
using System.Text.Json.Nodes;
using MandoCode.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Opt-in Windows integration checks against a real WebView2, with an isolated browser profile.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form { Width = 900, Height = 700, ShowInTaskbar = false, Opacity = 0 };
        using var browser = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(browser);
        var exitCode = 1;
        var profile = Path.Combine(Path.GetTempPath(), "MandoBrowserSmoke-" + Guid.NewGuid().ToString("N"));
        form.Shown += async (_, _) =>
        {
            try
            {
                await browser.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: profile));
                await CheckBrowserAsync(browser.CoreWebView2).WaitAsync(TimeSpan.FromSeconds(45));
                exitCode = 0;
                Console.WriteLine("PASS: real WebView2 DOM, pointer, keyboard, repeated clicks, focused observations, " +
                    "forms, scrolling, navigation, fresh assets on reload, diagnostics, and argument escaping.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { browser.Dispose(); form.Close(); }
        };
        Application.Run(form);
        // Browser shutdown can briefly retain profile files; the unique profile never affects the app.
        try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return exitCode;
    }

    private static async Task CheckBrowserAsync(CoreWebView2 core)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        core.SetVirtualHostNameToFolderMapping("preview.mandocode.local", root, CoreWebView2HostResourceAccessKind.Allow);
        var errors = new List<string>();
        var receiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        receiver.DevToolsProtocolEventReceived += (_, args) => errors.Add(args.ParameterObjectAsJson);
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await NavigateAsync(core, "https://preview.mandocode.local/index.html");

        async Task<JsonObject> Run(string operation, string? selector = null, string? value = null, int offset = 0, int deltaY = 0, string? observe = null)
        {
            var json = await core.ExecuteScriptAsync(DesktopPreviewScripts.Build(new(operation, root, Selector: selector, Value: value, Offset: offset, DeltaY: deltaY, Observe: observe)));
            return JsonNode.Parse(json) as JsonObject ?? throw new Exception("No result: " + json);
        }
        async Task Click(string selector, int count = 1)
        {
            // Mirrors the host loop: every repeat re-locates its target before pressing.
            for (var attempt = 0; attempt < count; attempt++)
            {
                var target = await Run("click", selector);
                Assert(target["ok"]!.GetValue<bool>(), target.ToJsonString());
                var x = target["x"]!.GetValue<double>();
                var y = target["y"]!.GetValue<double>();
                foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = type == "mouseMoved" ? "none" : "left", clickCount = 1 }));
            }
        }
        async Task PressKey(string name, string? modifiers = null, int holdMs = 0)
        {
            Assert(DesktopPreviewKeys.TryResolve(name, out var key, out var keyModifiers, out var keyError), keyError);
            Assert(DesktopPreviewKeys.TryParseModifiers(modifiers, out var mask, out var modifierError), modifierError);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", DesktopPreviewKeys.BuildKeyEvent(key, mask | keyModifiers, down: true));
            if (holdMs > 0) await Task.Delay(holdMs);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", DesktopPreviewKeys.BuildKeyEvent(key, mask | keyModifiers, down: false));
        }

        var state = await Run("inspect");
        Assert(state["ok"]!.GetValue<bool>(), "Snapshot failed");
        Assert(state["controls"]!.AsArray().Any(n => n?["selector"]?.GetValue<string>() == "#increment"), "No usable selector");
        Assert(!state["text"]!.GetValue<string>().Contains("INVISIBLE TEXT"), "Hidden text was included");
        Assert(state["canvasCount"]!.GetValue<int>() == 1, "Canvas limitation not reported");
        Assert(state["nextOffset"] != null, "Control pagination missing");
        Assert((await Run("inspect", offset: 40))["controls"]!.AsArray().Count > 0, "Control pagination failed");
        Assert(!(await Run("click", ".duplicate"))["ok"]!.GetValue<bool>(), "Ambiguous click accepted");
        Assert(!(await Run("click", "#covered"))["ok"]!.GetValue<bool>(), "Covered click accepted");
        Assert(!(await Run("click", "#disabled"))["ok"]!.GetValue<bool>(), "Disabled click accepted");
        Assert(!(await Run("click", "#hidden"))["ok"]!.GetValue<bool>(), "Hidden click accepted");

        await Click("#increment");
        var clicked = await Run("inspect", "#status");
        Assert(clicked["text"]!.GetValue<string>() == "Count 1 trusted=true", "Browser click did not fire trusted input");

        // A repeated click has to land every time, and a focused observation has to stay small.
        await Click("#increment", 5);
        var observed = await Run("observe", observe: "#status");
        Assert(observed["matched"]!.GetValue<bool>(), "Focused observation did not match");
        Assert(observed["element"]!["text"]!.GetValue<string>() == "Count 6 trusted=true", "Repeated clicks did not all land");
        Assert(observed["controls"] == null && observed["viewport"] == null, "Focused observation returned a full snapshot");
        Assert(!(await Run("observe", observe: "#nothing-here"))["matched"]!.GetValue<bool>(), "Absent element reported as matched");
        Assert(!(await Run("observe", observe: ".duplicate"))["ok"]!.GetValue<bool>(), "Ambiguous observation accepted");

        // Keyboard input: text entry, editing keys, Enter-to-submit, Tab focus, and a bounded hold.
        Assert((await Run("focus", "#typed"))["ok"]!.GetValue<bool>(), "Focus failed");
        foreach (var character in "Hi!") await PressKey(character.ToString());
        await PressKey("Backspace");
        Assert((await Run("observe", observe: "#typed"))["element"]!["value"]!.GetValue<string>() == "Hi",
            "Typed characters or Backspace did not reach the focused input");
        Assert((await Run("inspect"))["keyboardFocus"]!.GetValue<string>() == "#typed", "Keyboard focus was not reported");
        await Run("focus", "#term");
        await PressKey("q");
        await PressKey("Enter");
        Assert((await Run("observe", observe: "#submitted"))["text"]!.GetValue<string>() == "submitted q", "Enter did not submit the form");
        await Run("focus", "#first");
        await PressKey("Tab");
        Assert((await Run("inspect"))["keyboardFocus"]!.GetValue<string>() == "#second", "Tab did not move keyboard focus");
        await PressKey("ArrowRight", holdMs: 120);
        Assert((await Run("observe", observe: "#held"))["text"]!.GetValue<string>() == "up", "A held key was not released");
        var keylog = (await Run("observe", observe: "#keylog"))["text"]!.GetValue<string>();
        Assert(keylog.Contains("[Backspace]") && keylog.Contains("[ArrowRight]") && !keylog.Contains("untrusted"),
            "Key events were not delivered as trusted input: " + keylog);
        await PressKey("e", "ctrl");
        Assert((await Run("observe", observe: "#keylog"))["text"]!.GetValue<string>().Contains("[e+ctrl]"), "Modifier chord was not delivered");
        Assert((await Run("observe", observe: "#typed"))["element"]!["value"]!.GetValue<string>() == "Hi", "A Ctrl chord typed a character");
        await Run("fill", "#name", "Ada");
        Assert((await Run("inspect", "#echo"))["text"]!.GetValue<string>() == "Ada", "Input event missing");
        await Run("select", "#choice", "b");
        Assert((await Run("inspect", "#selection"))["text"]!.GetValue<string>() == "b", "Select change event missing");
        Assert(!(await Run("select", "#choice", "disabled"))["ok"]!.GetValue<bool>(), "Disabled option accepted");

        var malicious = "');window.pwned=true;//\"</script>";
        await Run("fill", "#name", malicious);
        Assert((await Run("inspect", "#echo"))["text"]!.GetValue<string>() == malicious, "Value was not treated as data");
        Assert(await core.ExecuteScriptAsync("window.pwned === undefined") == "true", "Argument injection executed");
        Assert(!(await Run("inspect", "']});window.pwned=true;//"))["ok"]!.GetValue<bool>(), "Invalid selector accepted");

        var hover = await Run("hover", "#hover");
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = hover["x"]!.GetValue<double>(), y = hover["y"]!.GetValue<double>() }));
        await Task.Delay(100);
        Assert((await Run("wait", "#tooltip"))["matched"]!.GetValue<bool>(), "Hover tooltip not visible");
        Assert(!(await Run("wait", "#never"))["matched"]!.GetValue<bool>(), "Absent element reported visible");
        await Run("scroll", "#footer");
        Assert((await Run("inspect"))["viewport"]!["scrollY"]!.GetValue<double>() > 0, "Scroll did not move viewport");
        Assert(errors.Any(e => e.Contains("fixture warning")), "Browser diagnostics not received");

        // An edited script must never come back from cache; a stale asset is what pushes people
        // into adding ?v=2 cache-busting query strings to their own project files.
        var assetRoot = Path.Combine(root, "cache");
        Directory.CreateDirectory(assetRoot);
        File.WriteAllText(Path.Combine(assetRoot, "page.html"), "<!doctype html><body><script src=\"asset.js\"></script>");
        try
        {
            File.WriteAllText(Path.Combine(assetRoot, "asset.js"), "document.body.textContent='asset v1';");
            await NavigateAsync(core, "https://preview.mandocode.local/cache/page.html");
            Assert((await Run("inspect"))["text"]!.GetValue<string>().Contains("asset v1"), "Fixture asset did not load");

            File.WriteAllText(Path.Combine(assetRoot, "asset.js"), "document.body.textContent='asset v2';");
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", """{"cacheDisabled":true}""");
            await ReloadAsync(core, ignoreCache: false);
            Assert((await Run("inspect"))["text"]!.GetValue<string>().Contains("asset v2"), "A plain reload served the cached script");

            File.WriteAllText(Path.Combine(assetRoot, "asset.js"), "document.body.textContent='asset v3';");
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", """{"cacheDisabled":false}""");
            await ReloadAsync(core, ignoreCache: true);
            Assert((await Run("inspect"))["text"]!.GetValue<string>().Contains("asset v3"), "A cache-bypassing reload served the cached script");
        }
        finally { Directory.Delete(assetRoot, recursive: true); }

        await NavigateAsync(core, "https://preview.mandocode.local/second.html");
        Assert((await Run("inspect"))["text"]!.GetValue<string>().Contains("Second page"), "Navigation did not load new DOM");
        // Snapshot execution must not accept arbitrary origins, even if the host guard is bypassed.
        core.NavigateToString("<h1>Unscoped document</h1>");
        await Task.Delay(150);
        Assert(!(await Run("inspect"))["ok"]!.GetValue<bool>(), "Non-project origin accepted");
        GC.KeepAlive(receiver);
    }

    private static async Task ReloadAsync(CoreWebView2 core, bool ignoreCache)
    {
        var completed = new TaskCompletionSource<bool>();
        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Done;
        try
        {
            if (ignoreCache) await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}""");
            else core.Reload();
            Assert(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Reload failed");
        }
        finally { core.NavigationCompleted -= Done; }
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url)
    {
        var completed = new TaskCompletionSource<bool>();
        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completed.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Done;
        try
        {
            core.Navigate(url);
            Assert(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Navigation failed");
        }
        finally { core.NavigationCompleted -= Done; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
