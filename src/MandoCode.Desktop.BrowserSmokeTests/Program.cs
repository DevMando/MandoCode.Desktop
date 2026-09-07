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
                await CheckTwoTabsAsync(form, browser).WaitAsync(TimeSpan.FromSeconds(20));
                exitCode = 0;
                Console.WriteLine("PASS: real WebView2 DOM, pointer, keyboard, repeated clicks, focused observations, " +
                    "forms, scrolling, navigation, fresh assets on reload, screenshots under changing window visibility, origin scoping, diagnostics, argument escaping, and independent background-tab DOM operations.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { browser.Dispose(); form.Close(); }
        };
        Application.Run(form);
        // Browser shutdown can briefly retain profile files; the unique profile never affects the app.
        try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return exitCode;
    }

    private static async Task CheckTwoTabsAsync(Form form, WebView2 first)
    {
        using var second = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(second);
        await second.EnsureCoreWebView2Async(first.CoreWebView2.Environment);
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures");
        const string origin = "https://preview.mandocode.local";
        second.CoreWebView2.SetVirtualHostNameToFolderMapping("preview.mandocode.local", root, CoreWebView2HostResourceAccessKind.DenyCors);
        await NavigateAsync(first.CoreWebView2, origin + "/index.html");
        await NavigateAsync(second.CoreWebView2, origin + "/index.html");
        // Match Desktop: changing visible controls leaves the captured WebView target intact.
        var capturedTarget = first.CoreWebView2;
        first.Visible = false;
        second.Visible = true;
        second.BringToFront();
        var result = JsonNode.Parse(await capturedTarget.ExecuteScriptAsync(DesktopPreviewScripts.Build(
            new("fill", root, Selector: "#name", Value: "only the captured tab", Origin: origin))))!;
        Assert(result["ok"]!.GetValue<bool>(), "Background target could not perform its DOM operation");
        Assert((await capturedTarget.ExecuteScriptAsync("document.querySelector('#name').value")).Contains("only the captured tab"), "Captured target did not change");
        Assert(await second.CoreWebView2.ExecuteScriptAsync("document.querySelector('#name').value") == "\"\"", "Selected tab was changed by another tab's action");
        await CheckFramesAsync(first.CoreWebView2, second.CoreWebView2, root);
        second.Dispose();
        first.Visible = true;
    }

    private static async Task CheckFramesAsync(CoreWebView2 target, CoreWebView2 selected, string root)
    {
        var frames = new BrowserFrames(target);
        var otherFrames = new BrowserFrames(selected);
        foreach (var core in new[] { target, selected })
            core.SetVirtualHostNameToFolderMapping("forms.mandocode.local", root, CoreWebView2HostResourceAccessKind.DenyCors);
        const string origin = "https://preview.mandocode.local";
        await NavigateAsync(target, origin + "/frame-host.html");
        await NavigateAsync(selected, origin + "/frame-host.html");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (frames.Describe().Count == 2 && otherFrames.Describe().Count == 2 &&
                frames.Describe().All(n => n?["state"]?.GetValue<string>() == "ready") &&
                otherFrames.Describe().All(n => n?["state"]?.GetValue<string>() == "ready")) break;
            await Task.Delay(50);
        }
        var parent = JsonNode.Parse(await target.ExecuteScriptAsync(DesktopPreviewScripts.Build(new("inspect", root, Origin: origin))))!;
        Assert(parent["uninspectedFrames"]!.GetValue<int>() == 1, "Parent did not disclose uninspected frames");
        var frameElement = JsonNode.Parse(await target.ExecuteScriptAsync(DesktopPreviewScripts.Build(new("inspect", root, Selector: "#contact", Origin: origin))))!;
        Assert(frameElement["ok"]!.GetValue<bool>() == false, "Iframe fallback text was misrepresented as frame document inspection");
        string ContactId(BrowserFrames registry) => registry.Describe().First(n => n?["url"]?.GetValue<string>()?.EndsWith("/frame-contact.html") == true)!["frameId"]!.GetValue<string>();
        var frameId = ContactId(frames);
        var inspect = await frames.ExecuteAsync(new("inspect", root, FrameId: frameId), CancellationToken.None);
        Assert(inspect["controls"]!.AsArray().Any(n => n?["selector"]?.GetValue<string>() == "#first"), "Cross-origin frame fields unavailable");
        foreach (var field in new[] { ("#first", "Alex"), ("#last", "Example") })
        {
            var filled = await frames.ExecuteAsync(new("fill", root, Selector: field.Item1, Value: field.Item2, FrameId: frameId), CancellationToken.None);
            Assert(filled["fieldVerification"]?["matches"]?.GetValue<bool>() == true, "Field did not retain placeholder value");
            var observed = await frames.ExecuteAsync(new("observe", root, Observe: field.Item1, FrameId: frameId), CancellationToken.None);
            Assert(observed["element"]?["value"]?.GetValue<string>() == field.Item2, "Read-back did not confirm value");
        }
        var submissions = await frames.ExecuteAsync(new("observe", root, Observe: "#submissions", FrameId: frameId), CancellationToken.None);
        Assert(submissions["text"]?.GetValue<string>() == "0", "Filling submitted the form");
        var untouched = await otherFrames.ExecuteAsync(new("observe", root, Observe: "#first", FrameId: ContactId(otherFrames)), CancellationToken.None);
        Assert(untouched["element"]?["value"]?.GetValue<string>() == "", "Selected tab was modified instead of the target");
        Assert(frames.Describe().Any(n => n?["parentFrameId"]?.GetValue<string>() == frameId), "Nested frame was not discovered");
        try
        {
            await otherFrames.ExecuteAsync(new("fill", root, Selector: "#first", Value: "wrong", FrameId: frameId), CancellationToken.None);
            throw new Exception("A frame ID from another tab was accepted");
        }
        catch (InvalidOperationException) { }
        await target.ExecuteScriptAsync("document.querySelector('#contact').src='https://forms.mandocode.local/frame-contact.html?new=1'");
        for (var attempt = 0; attempt < 100 && frames.Describe().Any(n => n?["frameId"]?.GetValue<string>() == frameId); attempt++) await Task.Delay(20);
        try
        {
            await frames.ExecuteAsync(new("fill", root, Selector: "#first", Value: "wrong", FrameId: frameId), CancellationToken.None);
            throw new Exception("Navigated frame accepted a stale action");
        }
        catch (InvalidOperationException) { }
        for (var attempt = 0; attempt < 100 && !frames.Describe().Any(n => n?["state"]?.GetValue<string>() == "ready" && n?["url"]?.GetValue<string>()?.Contains("?new=1") == true); attempt++) await Task.Delay(20);
        var replacementId = frames.Describe().First(n => n?["url"]?.GetValue<string>()?.Contains("?new=1") == true)!["frameId"]!.GetValue<string>();
        await target.ExecuteScriptAsync("document.querySelector('#contact').remove()");
        try
        {
            await frames.ExecuteAsync(new("fill", root, Selector: "#first", Value: "wrong", FrameId: replacementId), CancellationToken.None);
            throw new Exception("Removed frame accepted a stale action");
        }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: cross-origin and nested frame discovery, placeholder fill/read-back, no submission, background-tab isolation, and removed-frame rejection.");
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

        const string previewOrigin = "https://preview.mandocode.local";
        async Task<JsonObject> RunAs(string? origin, string operation, string? selector = null, string? value = null, int offset = 0, int deltaY = 0, string? observe = null)
        {
            var json = await core.ExecuteScriptAsync(DesktopPreviewScripts.Build(
                new(operation, root, Selector: selector, Value: value, Offset: offset, DeltaY: deltaY, Observe: observe, Origin: origin)));
            return JsonNode.Parse(json) as JsonObject ?? throw new Exception("No result: " + json);
        }
        Task<JsonObject> Run(string operation, string? selector = null, string? value = null, int offset = 0, int deltaY = 0, string? observe = null) =>
            RunAs(previewOrigin, operation, selector, value, offset, deltaY, observe);
        async Task<byte[]> CaptureFrom(bool fromSurface)
        {
            var captured = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                JsonSerializer.Serialize(new { format = "png", fromSurface, captureBeyondViewport = false }));
            var data = (JsonNode.Parse(captured) as JsonObject)?["data"]?.GetValue<string>();
            return string.IsNullOrEmpty(data) ? [] : Convert.FromBase64String(data);
        }
        async Task<byte[]> Capture(JsonObject? clip)
        {
            object parameters = clip == null ? new { format = "png" } : new
            {
                format = "png",
                clip = new
                {
                    x = clip["x"]!.GetValue<double>(), y = clip["y"]!.GetValue<double>(),
                    width = clip["width"]!.GetValue<double>(), height = clip["height"]!.GetValue<double>(), scale = 1,
                },
            };
            var captured = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(parameters));
            var data = (JsonNode.Parse(captured) as JsonObject)?["data"]?.GetValue<string>();
            Assert(!string.IsNullOrEmpty(data), "No screenshot data returned");
            return Convert.FromBase64String(data!);
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
        var covered = await Run("click", "#covered");
        Assert(!covered["ok"]!.GetValue<bool>(), "Covered click accepted");
        Assert(covered["error"]!.GetValue<string>().Contains("#cover"), "Covered click did not name what covers the target");
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

        // The origin guard is the whole basis for allowing development servers: a script must only
        // ever run on the one origin the host opened.
        Assert(!(await RunAs("https://preview.mandocode.local.evil.test", "inspect"))["ok"]!.GetValue<bool>(), "A lookalike origin was accepted");
        Assert(!(await RunAs(null, "inspect"))["ok"]!.GetValue<bool>(), "A missing origin was accepted");

        // Screenshots: real PNG bytes, and clipping to one element captures less than the page.
        var full = await Capture(null);
        Assert(full.Length > 100 && full[0] == 0x89 && full[1] == 0x50 && full[2] == 0x4E && full[3] == 0x47,
            $"Full screenshot was not a PNG ({full.Length} bytes)");
        var bounds = await Run("bounds", "#increment");
        Assert(bounds["ok"]!.GetValue<bool>(), "Bounds lookup failed: " + bounds.ToJsonString());
        Assert(bounds["width"]!.GetValue<double>() > 0 && bounds["height"]!.GetValue<double>() > 0, "Bounds had no area");
        var clipped = await Capture(bounds);
        Assert(clipped.Length < full.Length, $"Clipping captured no less than the full page ({clipped.Length} vs {full.Length})");
        Assert(!(await Run("bounds", "#hidden"))["ok"]!.GetValue<bool>(), "A hidden element was accepted for capture");

        // Capture while minimized varies with runtime/compositor state. The contract is a valid
        // image or a bounded timeout, not that a particular runtime must hang.
        var host = Application.OpenForms[0]!;
        async Task<(string Outcome, int Bytes)> TimedCapture()
        {
            try
            {
                var bytes = await CaptureFrom(true).WaitAsync(TimeSpan.FromSeconds(4));
                Assert(bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47,
                    "Capture returned invalid image bytes");
                return ("captured", bytes.Length);
            }
            catch (TimeoutException) { return ("hung", 0); }
        }
        host.Opacity = 1;
        await Task.Delay(500);
        var shown = await TimedCapture();
        Assert(shown.Outcome == "captured" && shown.Bytes > 20000,
            $"A visible preview did not capture a painted page ({shown.Outcome}, {shown.Bytes} bytes)");

        host.WindowState = FormWindowState.Minimized;
        await Task.Delay(500);
        var minimized = await TimedCapture();
        Assert(minimized.Outcome == "hung" || minimized.Outcome == "captured" && minimized.Bytes >= 8,
            "Minimized capture neither returned a valid image nor reached the bounded timeout.");
        host.WindowState = FormWindowState.Normal;
        host.Opacity = 0;
        await Task.Delay(500);

        // The blank heuristic has to separate these two in the real thing, not just in theory.
        var painted = await CaptureFrom(true);
        Assert(!DesktopPreviewTools.LooksBlank(painted.Length, Viewport(900, 700)), "A painted page was flagged blank");
        Assert(DesktopPreviewTools.LooksBlank(3160, Viewport(900, 700)), "A blank-sized capture was not flagged");

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

    private static JsonObject Viewport(int width, int height) =>
        new() { ["viewport"] = new JsonObject { ["width"] = width, ["height"] = height } };

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
