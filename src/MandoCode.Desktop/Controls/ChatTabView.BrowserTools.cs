using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MandoCode.Desktop.Services;
using MandoCode.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace MandoCode.Desktop;

public sealed partial class ChatTabView
{
    private readonly CancellationTokenSource _previewAutomationLifetime = new();
    internal static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetLeftPart(UriPartial.Authority) : null;

    private bool IsAllowedPreviewUrl(BrowserTab tab, string? url) =>
        !tab.Closed && (url == "about:blank" || OriginOf(url) is { } origin &&
        (tab.External || string.Equals(origin, tab.Origin, StringComparison.OrdinalIgnoreCase)));

    private async Task<string> DispatchPreviewRequestAsync(DesktopPreviewRequest request, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _previewAutomationLifetime.Token);
        var token = lifetime.Token;
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        using var registration = token.Register(() =>
        {
            if (Volatile.Read(ref started) == 0) completion.TrySetCanceled(token);
        });
        async void Run()
        {
            Interlocked.Exchange(ref started, 1);
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(await ExecutePreviewRequestAsync(request, token));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetResult(DesktopPreviewTools.Failure(ex.Message)); }
        }
        if (_dispatcher.HasThreadAccess) Run();
        else if (!_dispatcher.TryEnqueue(Run)) completion.TrySetResult(DesktopPreviewTools.Failure("The preview UI is unavailable."));
        return await completion.Task;
    }

    private async Task<string> ExecutePreviewRequestAsync(DesktopPreviewRequest request, CancellationToken token)
    {
        if (request.Operation == "list-tabs")
            return JsonSerializer.Serialize(new { ok = true, tabs = _browserTabs.Where(t => !t.Closed).Select(t => new {
                tabId = t.Id, title = t.View.CoreWebView2?.DocumentTitle, url = t.View.CoreWebView2?.Source,
                selected = t == _selectedBrowserTab && _previewOpen && _browserPreview }) });
        BrowserTab tab;
        if (request.TabId != null)
        {
            var found = _browserTabs.FirstOrDefault(t => t.Id == request.TabId && !t.Closed);
            if (found == null) return DesktopPreviewTools.Failure("The requested browser tab is closed or unknown. No other tab was used.");
            tab = found;
        }
        else if (request.Operation is "open" or "open-browser")
        {
            if (_previewDirty) return DesktopPreviewTools.Failure("Save or discard the file preview's unsaved edits before opening the browser.");
            tab = await CreateBrowserTabAsync(request.Operation == "open-browser", token);
        }
        else return DesktopPreviewTools.Failure("An explicit tabId is required. List browser tabs first.");
        using var tabLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, tab.Lifetime.Token);
        token = tabLifetime.Token;
        void CheckProject()
        {
            token.ThrowIfCancellationRequested();
            if (tab.Closed || _shutDown || !string.Equals(request.ProjectRoot, _controller.ProjectRootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The tab closed or changed projects. Open the current project's preview first.");
        }
        CheckProject();
        if ((!tab.External || request.FullPath != null) && tab.ProjectRoot != request.ProjectRoot)
            return DesktopPreviewTools.Failure("This browser tab belongs to a different project. No navigation or action was performed.");
        tab.AgentRequests++;
        try
        {
            if (tab.View.CoreWebView2 is { } existingCore) existingCore.Settings.AreDefaultScriptDialogsEnabled = false;
            if (request.Operation == "open-browser") tab.External = true;
            if (request.Operation is "open" or "open-browser")
                await NavigateBrowserTabAsync(tab, request.Url, request.FullPath, token);
            CheckProject();
            var core = tab.View.CoreWebView2;
            if (tab.Closed || core == null ||
                (!tab.External && !string.Equals(tab.ProjectRoot, request.ProjectRoot, StringComparison.OrdinalIgnoreCase)))
                return DesktopPreviewTools.Failure("No browser preview is open for this project. Call open_desktop_preview first.");
            if (request.Operation == "refresh")
                await NavigatePreviewConfirmedAsync(core, () => ReloadPreviewFreshAsync(tab, core), token);
            await WaitForPreviewNavigationAsync(tab, token);
            CheckProject();
            if (!IsAllowedPreviewUrl(tab, core.Source)) return DesktopPreviewTools.Failure("The active document is outside the project preview.");

            if (request.Operation == "list-frames")
                return AddPreviewDiagnostics(tab, new JsonObject { ["ok"] = true, ["frames"] = tab.Frames?.Describe(),
                    ["note"] = "Frame documents have not been inspected. Inspect the relevant frameId before concluding fields are absent." });

            if (request.Operation == "wait")
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    CheckProject();
                    var state = await ExecutePreviewScriptAsync(tab, core, request, token);
                    if (state["ok"]?.GetValue<bool>() != true || state["matched"]?.GetValue<bool>() == true)
                        return AddPreviewDiagnostics(tab, state);
                    await Task.Delay(200, token);
                    await WaitForPreviewNavigationAsync(tab, token);
                }
                return DesktopPreviewTools.Failure("The expected element/text was not visible within 10 seconds. The action was not repeated; inspect the page to determine its state.");
            }

            if (request.Operation is "click" or "hover")
            {
                var version = tab.DocumentVersion;
                var target = await ExecutePreviewScriptAsync(tab, core, request, token);
                if (target["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(tab, target);
                CheckProject();
                if (version != tab.DocumentVersion || !IsAllowedPreviewUrl(tab, core.Source))
                    return DesktopPreviewTools.Failure("The page navigated before the action. Inspect its new state.");
                var x = target["x"]!.GetValue<double>();
                var y = target["y"]!.GetValue<double>();
                await MovePreviewPointerAsync(core, x, y);
                CheckProject();
                if (version != tab.DocumentVersion) return DesktopPreviewTools.Failure("The page navigated while moving the pointer. Inspect the new page before clicking.");
                var completedClicks = 0;
                if (request.Operation == "click")
                {
                    var confirmed = await ExecutePreviewScriptAsync(tab, core, request, token);
                    if (confirmed["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(tab, confirmed);
                    if (Math.Abs(confirmed["x"]!.GetValue<double>() - x) > 1 || Math.Abs(confirmed["y"]!.GetValue<double>() - y) > 1)
                        return DesktopPreviewTools.Failure("The element moved after hover. Inspect its new state before clicking.");
                    for (var attempt = 0; attempt < request.Count; attempt++)
                    {
                        CheckProject();
                        if (attempt > 0)
                        {
                            // A repeat re-locates its target: the element it just clicked may have
                            // moved, been replaced, or become covered. Stop and report what landed
                            // rather than clicking a stale point on the page.
                            JsonObject next;
                            try { next = await ExecutePreviewScriptAsync(tab, core, request, token); }
                            catch (InvalidOperationException ex) { return PartialClicks(tab, request, completedClicks, ex.Message); }
                            if (next["ok"]?.GetValue<bool>() != true)
                                return PartialClicks(tab, request, completedClicks, next["error"]?.GetValue<string>() ?? "The element is no longer clickable.");
                            x = next["x"]!.GetValue<double>();
                            y = next["y"]!.GetValue<double>();
                            await MovePreviewPointerAsync(core, x, y);
                        }
                        try
                        {
                            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 }));
                        }
                        finally
                        {
                            // Always release a dispatched button, even if the turn was cancelled.
                            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }));
                        }
                        request.Progress?.Note(++completedClicks);
                    }
                }
                CheckProject();
                await WaitForPreviewNavigationAsync(tab, token);
                var state = await ObservePreviewAsync(tab, core, request, token);
                state["actionDispatched"] = request.Operation;
                if (request.Operation == "click" && request.Count > 1)
                {
                    state["clicksRequested"] = request.Count;
                    state["clicksCompleted"] = completedClicks;
                }
                return AddPreviewDiagnostics(tab, state);
            }

            if (request.Operation == "key") return await ExecutePreviewKeyAsync(tab, core, request, CheckProject, token);

            if (request.Operation == "screenshot")
            {
                if (tab != _selectedBrowserTab || !_previewOpen || !_browserPreview)
                    return DesktopPreviewTools.Failure("The targeted tab is not visible. Use DOM inspection or ask the user to select that tab for a screenshot.");
                object parameters = new { format = "png" };
                var state = await ExecutePreviewScriptAsync(tab, core, request with { Operation = "pagestate" }, token);
                if (!string.IsNullOrWhiteSpace(request.Selector))
                {
                    var bounds = await ExecutePreviewScriptAsync(tab, core, request with { Operation = "bounds" }, token);
                    if (bounds["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(tab, bounds);
                    parameters = new
                    {
                        format = "png",
                        clip = new
                        {
                            x = bounds["x"]!.GetValue<double>(),
                            y = bounds["y"]!.GetValue<double>(),
                            width = bounds["width"]!.GetValue<double>(),
                            height = bounds["height"]!.GetValue<double>(),
                            scale = 1,
                        },
                    };
                    state["captured"] = request.Selector;
                }
                CheckProject();
                string captured;
                try
                {
                    // A minimized window has no compositor surface to read, and the browser never
                    // answers rather than failing. Bound it so that costs seconds and a clear
                    // explanation instead of the whole operation deadline and a vague timeout.
                    captured = await CaptureWithDeadlineAsync(core, parameters, token);
                }
                catch (TimeoutException)
                {
                    return DesktopPreviewTools.Failure(
                        "The preview could not be captured, which usually means the app window is minimized. " +
                        "Ask the user to restore the window, or continue with inspect and observe and say that " +
                        "visual layout could not be checked.");
                }
                var data = (JsonNode.Parse(captured) as JsonObject)?["data"]?.GetValue<string>();
                if (string.IsNullOrEmpty(data))
                    return DesktopPreviewTools.Failure("The browser did not return a screenshot. The preview may be hidden or still loading.");
                // Only the visible viewport is captured, so an enormous page cannot produce an
                // enormous image, and what the model sees is what a person would see in the pane.
                state["image"] = data;
                state["imageScope"] = "The visible preview viewport at the moment of capture.";
                return AddPreviewDiagnostics(tab, state);
            }

            var operation = request.Operation is "open" or "open-browser" or "refresh" ? request with { Operation = "inspect" } : request;
            return AddPreviewDiagnostics(tab, await ExecutePreviewScriptAsync(tab, core, operation, token));
        }
        catch (OperationCanceledException) when (tab.Closed)
        {
            return DesktopPreviewTools.Failure("The targeted browser tab closed during the operation. No other tab was used. An already dispatched action may have occurred.");
        }
        catch (InvalidOperationException ex)
        {
            // These are the failures that leave the page in an unknown state — a navigation mid-script,
            // a target that moved. Letting them reach the dispatcher's generic catch would strip the
            // tab and the console output, which is precisely what deciding whether to retry needs.
            return AddPreviewDiagnostics(tab, new JsonObject { ["ok"] = false, ["error"] = ex.Message });
        }
        finally
        {
            tab.AgentRequests--;
            if (!_shutDown && !tab.Closed && tab.View.CoreWebView2 is { } activeCore)
                activeCore.Settings.AreDefaultScriptDialogsEnabled = tab.AgentRequests == 0;
        }
    }

    private async Task<string> ExecutePreviewKeyAsync(BrowserTab tab, CoreWebView2 core, DesktopPreviewRequest request, Action checkProject, CancellationToken token)
    {
        var key = request.Key!;
        var version = tab.DocumentVersion;
        if (!string.IsNullOrWhiteSpace(request.Selector))
        {
            var focus = await ExecutePreviewScriptAsync(tab, core, request with { Operation = "focus" }, token);
            if (focus["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(tab, focus);
            checkProject();
            if (version != tab.DocumentVersion) return DesktopPreviewTools.Failure("The page navigated while taking keyboard focus. Inspect its new state.");
        }

        var completed = 0;
        for (var attempt = 0; attempt < request.Count; attempt++)
        {
            checkProject();
            await DispatchPreviewKeyAsync(core, key, request.Modifiers, down: true);
            try
            {
                if (request.HoldMs > 0) await Task.Delay(request.HoldMs, token);
            }
            finally
            {
                // Always release a key this call pressed, even if the turn was cancelled. No key
                // outlives the call that pressed it, so a page is never left with one stuck down.
                await DispatchPreviewKeyAsync(core, key, request.Modifiers, down: false);
            }
            request.Progress?.Note(++completed);
        }
        checkProject();
        await WaitForPreviewNavigationAsync(tab, token);
        var state = await ObservePreviewAsync(tab, core, request, token);
        state["actionDispatched"] = "key " + key.Name;
        if (request.Count > 1)
        {
            state["pressesRequested"] = request.Count;
            state["pressesCompleted"] = completed;
        }
        return AddPreviewDiagnostics(tab, state);
    }

    /// <summary>Screenshot capture, bounded. See the call site for why the browser can never answer.</summary>
    private static async Task<string> CaptureWithDeadlineAsync(CoreWebView2 core, object parameters, CancellationToken token)
    {
        var operation = core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", JsonSerializer.Serialize(parameters));
        async Task<string> Awaited() => await operation;
        return await Awaited().WaitAsync(TimeSpan.FromSeconds(6), token);
    }

    private static async Task DispatchPreviewKeyAsync(CoreWebView2 core, BrowserKey key, int modifiers, bool down) =>
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", DesktopPreviewKeys.BuildKeyEvent(key, modifiers, down));

    private static async Task MovePreviewPointerAsync(CoreWebView2 core, double x, double y) =>
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x, y }));

    /// <summary>Reads back only the element the caller asked about, or the whole page when it named none.</summary>
    private Task<JsonObject> ObservePreviewAsync(BrowserTab tab, CoreWebView2 core, DesktopPreviewRequest request, CancellationToken token) =>
        ExecutePreviewScriptAsync(tab, core, request with { Operation = "inspect", Selector = null, Offset = 0 }, token);

    private string PartialClicks(BrowserTab tab, DesktopPreviewRequest request, int completed, string reason) =>
        AddPreviewDiagnostics(tab, new JsonObject
        {
            ["ok"] = false,
            ["clicksRequested"] = request.Count,
            ["clicksCompleted"] = completed,
            ["error"] = $"Stopped after {completed} of {request.Count} clicks: {reason} The remaining clicks were not " +
                "attempted and none were replayed. Inspect the page before deciding what to do next.",
        });

    /// <summary>
    /// Reloads past the browser cache. A preview exists to show what is on disk right now, so an
    /// edited script or stylesheet must never come back from cache — that is exactly what pushes
    /// people into adding ?v=2 cache-busting query strings to their own project files.
    /// </summary>
    private async Task ReloadPreviewFreshAsync(BrowserTab tab, CoreWebView2 core)
    {
        try { await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}"""); }
        catch (Exception ex)
        {
            NotePreviewDiagnostic(tab, "cache-bypass-unavailable", "A cache-bypassing reload failed; reloading normally: " + ex.Message);
            core.Reload();
        }
    }

    private async Task<JsonObject> ExecutePreviewScriptAsync(BrowserTab tab, CoreWebView2 core, DesktopPreviewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (tab.Closed || (!tab.External && tab.ProjectRoot != request.ProjectRoot) ||
            _controller.ProjectRootPath != request.ProjectRoot || !IsAllowedPreviewUrl(tab, core.Source))
            throw new InvalidOperationException("The preview changed. Open and inspect the current project page first.");
        var version = tab.DocumentVersion;
        JsonObject state;
        if (request.FrameId is { } frameId && frameId != "main")
            state = await (tab.Frames ?? throw new InvalidOperationException("Frame inspection is unavailable."))
                .ExecuteAsync(request, token);
        else
        {
            var json = await core.ExecuteScriptAsync(DesktopPreviewScripts.Build(request with { Origin = OriginOf(core.Source) }));
            state = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("The page did not return a DOM observation.");
            state["frameId"] = "main";
        }
        token.ThrowIfCancellationRequested();
        if (_controller.ProjectRootPath != request.ProjectRoot || (!tab.External && tab.ProjectRoot != request.ProjectRoot) || tab.Closed)
            throw new InvalidOperationException("The project or preview changed during the operation. Inspect before retrying; an action may have occurred.");
        if (version != tab.DocumentVersion) throw new InvalidOperationException("The page navigated during the operation. Inspect before retrying; an action may have occurred.");
        state["availableFrames"] = tab.Frames?.Describe();
        return state;
    }

    private string AddPreviewDiagnostics(BrowserTab tab, JsonObject state)
    {
        state["tabId"] = tab.Id;
        state["browserDiagnostics"] = JsonSerializer.SerializeToNode(tab.Diagnostics.ToArray());
        state["diagnosticsAvailable"] = JsonSerializer.SerializeToNode(tab.DiagnosticDomains);
        state["assetCache"] = tab.CacheBypassed
            ? "Bypassed. Every load reads the current file, so edited scripts and styles need no cache-busting query string."
            : "Browser default; a refresh still asks for a cache-bypassing reload.";
        state["diagnosticsScope"] = "Most recent 12 entries since the current navigation; no errors is not proof of correctness.";
        return state.ToJsonString();
    }

    private void NotePreviewDiagnostic(BrowserTab tab, string kind, string message)
    {
        while (tab.Diagnostics.Count >= 12) tab.Diagnostics.Dequeue();
        tab.Diagnostics.Enqueue(new { kind, message = message.Length > 1000 ? message[..1000] : message });
    }

    private async Task InitializePreviewAutomationAsync(BrowserTab tab, CoreWebView2 core)
    {
        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedPreviewUrl(tab, args.Uri))
            {
                args.Cancel = true;
                NotePreviewDiagnostic(tab, "blocked-navigation", args.Uri);
                if (tab.AgentRequests == 0 && args.IsUserInitiated && ShellOpen.Try(args.Uri) is { } ex)
                    _transcript.Append(_html.Warn($"Couldn't open link: {ex.Message}"));
                return;
            }
            if (tab.External) tab.Origin = OriginOf(args.Uri);
            tab.DocumentVersion++;
            // Redirects share a navigation ID; keep their waiter until the final document loads.
            if (tab.NavigationId == args.NavigationId && tab.Navigation is { Task.IsCompleted: false }) return;
            tab.NavigationId = args.NavigationId;
            tab.Navigation?.TrySetResult("Navigation was superseded. Inspect the current page.");
            tab.Navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.Diagnostics.Clear();
        };
        // Ordered after the blocking handler above: a navigation that is cancelled there never
        // replaces the document, so frame IDs must survive it rather than be cleared.
        tab.Frames = new BrowserFrames(core);
        core.NavigationCompleted += (_, args) =>
        {
            UpdateBrowserChrome(tab);
            if (args.NavigationId != tab.NavigationId) return;
            var error = args.IsSuccess && args.HttpStatusCode < 400 ? null : $"Preview navigation failed: {args.WebErrorStatus}, HTTP {args.HttpStatusCode}.";
            if (error != null) NotePreviewDiagnostic(tab, "navigation-error", error);
            tab.Navigation?.TrySetResult(error);
        };
        core.NewWindowRequested += (sender, args) =>
        {
            args.Handled = true;
            NotePreviewDiagnostic(tab, "blocked-popup", args.Uri);
            if (tab.AgentRequests == 0 && args.IsUserInitiated) _ = OpenUserBrowserUrlAsync(args.Uri);
        };
        core.DownloadStarting += (_, args) =>
        {
            if (tab.AgentRequests == 0) return;
            args.Cancel = true;
            NotePreviewDiagnostic(tab, "blocked-download", "Agent interaction attempted a download; no download was started.");
        };
        // Page alerts must not hang an agent turn behind a native modal dialog.
        core.Settings.AreDefaultScriptDialogsEnabled = tab.AgentRequests == 0;
        core.ScriptDialogOpening += (_, args) =>
        {
            if (tab.AgentRequests > 0) NotePreviewDiagnostic(tab, "script-dialog-dismissed", args.Message);
        };
        foreach (var eventName in new[] { "Runtime.consoleAPICalled", "Runtime.exceptionThrown", "Log.entryAdded", "Network.loadingFailed" })
        {
            var receiver = core.GetDevToolsProtocolEventReceiver(eventName);
            receiver.DevToolsProtocolEventReceived += (_, args) =>
            {
                if (!IsAllowedPreviewUrl(tab, core.Source)) return;
                if (eventName == "Runtime.consoleAPICalled")
                {
                    using var message = JsonDocument.Parse(args.ParameterObjectAsJson);
                    var type = message.RootElement.GetProperty("type").GetString();
                    if (type is not ("error" or "warning" or "assert")) return;
                }
                NotePreviewDiagnostic(tab, eventName, args.ParameterObjectAsJson);
            };
            tab.EventReceivers.Add(receiver);
        }
        foreach (var domain in new[] { "Runtime", "Log", "Network" })
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(domain + ".enable", "{}");
                tab.DiagnosticDomains[domain] = true;
            }
            catch (Exception ex)
            {
                tab.DiagnosticDomains[domain] = false;
                NotePreviewDiagnostic(tab, "diagnostics-unavailable", domain + ": " + ex.Message);
            }
        }
        // The preview must show the files as they are on disk. Without this, an edited stylesheet or
        // script can be served from cache on reload and the agent reviews the previous build.
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", """{"cacheDisabled":true}""");
            tab.CacheBypassed = true;
        }
        catch (Exception ex)
        {
            tab.CacheBypassed = false;
            NotePreviewDiagnostic(tab, "cache-bypass-unavailable", ex.Message);
        }
    }

    private async Task WaitForPreviewNavigationAsync(BrowserTab tab, CancellationToken token)
    {
        if (tab.Navigation == null) return;
        var error = await tab.Navigation.Task.WaitAsync(token);
        if (error != null) throw new InvalidOperationException(error);
    }

    private static async Task NavigatePreviewConfirmedAsync(CoreWebView2 core, Func<Task> navigate, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Started(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId != null && navigationId != args.NavigationId)
                completion.TrySetResult("Another navigation replaced the requested page. Inspect its current state.");
            navigationId ??= args.NavigationId;
        }
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (navigationId == args.NavigationId)
                completion.TrySetResult(args.IsSuccess && args.HttpStatusCode < 400 ? null : $"Preview navigation failed: {args.WebErrorStatus}, HTTP {args.HttpStatusCode}.");
        }
        core.NavigationStarting += Started;
        core.NavigationCompleted += Completed;
        try
        {
            token.ThrowIfCancellationRequested();
            await navigate();
            var error = await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            if (error != null) throw new InvalidOperationException(error);
        }
        finally
        {
            core.NavigationStarting -= Started;
            core.NavigationCompleted -= Completed;
        }
    }
}
