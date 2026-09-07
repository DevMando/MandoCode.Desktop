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
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _previewEventReceivers = [];
    private readonly Queue<object> _previewDiagnostics = new();
    private readonly Dictionary<string, bool> _previewDiagnosticDomains = new();
    private string? _previewMappedRoot;
    private int _agentBrowserRequests;
    private long _previewDocumentVersion;
    private ulong _previewNavigationId;
    private bool _previewCacheBypassed;
    private TaskCompletionSource<string?>? _previewNavigation;

    /// <summary>
    /// The one origin this preview was opened on — the project virtual host, or a loopback
    /// development server. Every script call and every navigation is checked against it, so
    /// widening the preview to dev servers never widens it to the network.
    /// </summary>
    private string? _previewOrigin;

    internal static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetLeftPart(UriPartial.Authority) : null;

    private bool IsAllowedPreviewUrl(string? url) =>
        _previewOrigin != null && OriginOf(url) is { } origin &&
        string.Equals(origin, _previewOrigin, StringComparison.OrdinalIgnoreCase);

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
        void CheckProject()
        {
            token.ThrowIfCancellationRequested();
            if (_shutDown || !string.Equals(request.ProjectRoot, _controller.ProjectRootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The tab closed or changed projects. Open the current project's preview first.");
        }
        CheckProject();
        _agentBrowserRequests++;
        try
        {
            if (PreviewBrowser.CoreWebView2 is { } existingCore) existingCore.Settings.AreDefaultScriptDialogsEnabled = false;
            if (request.Operation == "open")
            {
                if (_previewDirty) return DesktopPreviewTools.Failure("The preview has unsaved user edits. Save or discard them before opening another preview.");
                if (request.Url != null) await OpenUrlPreviewAsync(request.Url, token);
                else await OpenFilePreviewAsync(ExplorerItem.ForFile(request.FullPath!, request.ProjectRoot), token);
            }
            CheckProject();
            var core = PreviewBrowser.CoreWebView2;
            if (!_previewOpen || !_browserPreview || core == null ||
                !string.Equals(_previewMappedRoot, request.ProjectRoot, StringComparison.OrdinalIgnoreCase))
                return DesktopPreviewTools.Failure("No browser preview is open for this project. Call open_desktop_preview first.");
            if (request.Operation == "refresh")
                await NavigatePreviewConfirmedAsync(core, () => ReloadPreviewFreshAsync(core), token);
            await WaitForPreviewNavigationAsync(token);
            CheckProject();
            if (!IsAllowedPreviewUrl(core.Source)) return DesktopPreviewTools.Failure("The active document is outside the project preview.");

            if (request.Operation == "wait")
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    CheckProject();
                    var state = await ExecutePreviewScriptAsync(core, request, token);
                    if (state["ok"]?.GetValue<bool>() != true || state["matched"]?.GetValue<bool>() == true)
                        return AddPreviewDiagnostics(state);
                    await Task.Delay(200, token);
                    await WaitForPreviewNavigationAsync(token);
                }
                return DesktopPreviewTools.Failure("The expected element/text was not visible within 10 seconds. The action was not repeated; inspect the page to determine its state.");
            }

            if (request.Operation is "click" or "hover")
            {
                var version = _previewDocumentVersion;
                var target = await ExecutePreviewScriptAsync(core, request, token);
                if (target["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(target);
                CheckProject();
                if (version != _previewDocumentVersion || !IsAllowedPreviewUrl(core.Source))
                    return DesktopPreviewTools.Failure("The page navigated before the action. Inspect its new state.");
                var x = target["x"]!.GetValue<double>();
                var y = target["y"]!.GetValue<double>();
                await MovePreviewPointerAsync(core, x, y);
                CheckProject();
                if (version != _previewDocumentVersion) return DesktopPreviewTools.Failure("The page navigated while moving the pointer. Inspect the new page before clicking.");
                var completedClicks = 0;
                if (request.Operation == "click")
                {
                    var confirmed = await ExecutePreviewScriptAsync(core, request, token);
                    if (confirmed["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(confirmed);
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
                            try { next = await ExecutePreviewScriptAsync(core, request, token); }
                            catch (InvalidOperationException ex) { return PartialClicks(request, completedClicks, ex.Message); }
                            if (next["ok"]?.GetValue<bool>() != true)
                                return PartialClicks(request, completedClicks, next["error"]?.GetValue<string>() ?? "The element is no longer clickable.");
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
                await WaitForPreviewNavigationAsync(token);
                var state = await ObservePreviewAsync(core, request, token);
                state["actionDispatched"] = request.Operation;
                if (request.Operation == "click" && request.Count > 1)
                {
                    state["clicksRequested"] = request.Count;
                    state["clicksCompleted"] = completedClicks;
                }
                return AddPreviewDiagnostics(state);
            }

            if (request.Operation == "key") return await ExecutePreviewKeyAsync(core, request, CheckProject, token);

            if (request.Operation == "screenshot")
            {
                object parameters = new { format = "png" };
                var state = await ExecutePreviewScriptAsync(core, request with { Operation = "pagestate" }, token);
                if (!string.IsNullOrWhiteSpace(request.Selector))
                {
                    var bounds = await ExecutePreviewScriptAsync(core, request with { Operation = "bounds" }, token);
                    if (bounds["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(bounds);
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
                return AddPreviewDiagnostics(state);
            }

            var operation = request.Operation is "open" or "refresh" ? request with { Operation = "inspect" } : request;
            return AddPreviewDiagnostics(await ExecutePreviewScriptAsync(core, operation, token));
        }
        finally
        {
            _agentBrowserRequests--;
            if (!_shutDown && PreviewBrowser.CoreWebView2 is { } activeCore)
                activeCore.Settings.AreDefaultScriptDialogsEnabled = _agentBrowserRequests == 0;
        }
    }

    private async Task<string> ExecutePreviewKeyAsync(CoreWebView2 core, DesktopPreviewRequest request, Action checkProject, CancellationToken token)
    {
        var key = request.Key!;
        var version = _previewDocumentVersion;
        if (!string.IsNullOrWhiteSpace(request.Selector))
        {
            var focus = await ExecutePreviewScriptAsync(core, request with { Operation = "focus" }, token);
            if (focus["ok"]?.GetValue<bool>() != true) return AddPreviewDiagnostics(focus);
            checkProject();
            if (version != _previewDocumentVersion) return DesktopPreviewTools.Failure("The page navigated while taking keyboard focus. Inspect its new state.");
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
        await WaitForPreviewNavigationAsync(token);
        var state = await ObservePreviewAsync(core, request, token);
        state["actionDispatched"] = "key " + key.Name;
        if (request.Count > 1)
        {
            state["pressesRequested"] = request.Count;
            state["pressesCompleted"] = completed;
        }
        return AddPreviewDiagnostics(state);
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
    private Task<JsonObject> ObservePreviewAsync(CoreWebView2 core, DesktopPreviewRequest request, CancellationToken token) =>
        ExecutePreviewScriptAsync(core, request with { Operation = "inspect", Selector = null, Offset = 0 }, token);

    private string PartialClicks(DesktopPreviewRequest request, int completed, string reason) =>
        AddPreviewDiagnostics(new JsonObject
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
    private async Task ReloadPreviewFreshAsync(CoreWebView2 core)
    {
        try { await core.CallDevToolsProtocolMethodAsync("Page.reload", """{"ignoreCache":true}"""); }
        catch (Exception ex)
        {
            NotePreviewDiagnostic("cache-bypass-unavailable", "A cache-bypassing reload failed; reloading normally: " + ex.Message);
            core.Reload();
        }
    }

    private async Task<JsonObject> ExecutePreviewScriptAsync(CoreWebView2 core, DesktopPreviewRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_previewOpen || !_browserPreview || _previewMappedRoot != request.ProjectRoot ||
            _controller.ProjectRootPath != request.ProjectRoot || !IsAllowedPreviewUrl(core.Source))
            throw new InvalidOperationException("The preview changed. Open and inspect the current project page first.");
        var version = _previewDocumentVersion;
        var json = await core.ExecuteScriptAsync(DesktopPreviewScripts.Build(request with { Origin = _previewOrigin }));
        token.ThrowIfCancellationRequested();
        if (_controller.ProjectRootPath != request.ProjectRoot || _previewMappedRoot != request.ProjectRoot || !_previewOpen || !_browserPreview)
            throw new InvalidOperationException("The project or preview changed during the operation. Inspect before retrying; an action may have occurred.");
        if (version != _previewDocumentVersion) throw new InvalidOperationException("The page navigated during the operation. Inspect before retrying; an action may have occurred.");
        return JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("The page did not return a DOM observation.");
    }

    private string AddPreviewDiagnostics(JsonObject state)
    {
        state["browserDiagnostics"] = JsonSerializer.SerializeToNode(_previewDiagnostics.ToArray());
        state["diagnosticsAvailable"] = JsonSerializer.SerializeToNode(_previewDiagnosticDomains);
        state["assetCache"] = _previewCacheBypassed
            ? "Bypassed. Every load reads the current file, so edited scripts and styles need no cache-busting query string."
            : "Browser default; a refresh still asks for a cache-bypassing reload.";
        state["diagnosticsScope"] = "Most recent 12 entries since the current navigation; no errors is not proof of correctness.";
        return state.ToJsonString();
    }

    private void NotePreviewDiagnostic(string kind, string message)
    {
        while (_previewDiagnostics.Count >= 12) _previewDiagnostics.Dequeue();
        _previewDiagnostics.Enqueue(new { kind, message = message.Length > 1000 ? message[..1000] : message });
    }

    private async Task InitializePreviewAutomationAsync(CoreWebView2 core)
    {
        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedPreviewUrl(args.Uri))
            {
                args.Cancel = true;
                NotePreviewDiagnostic("blocked-navigation", args.Uri);
                if (_agentBrowserRequests == 0 && args.IsUserInitiated && ShellOpen.Try(args.Uri) is { } ex)
                    _transcript.Append(_html.Warn($"Couldn't open link: {ex.Message}"));
                return;
            }
            _previewDocumentVersion++;
            // Redirects share a navigation ID; keep their waiter until the final document loads.
            if (_previewNavigationId == args.NavigationId && _previewNavigation is { Task.IsCompleted: false }) return;
            _previewNavigationId = args.NavigationId;
            _previewNavigation?.TrySetResult("Navigation was superseded. Inspect the current page.");
            _previewNavigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _previewDiagnostics.Clear();
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (args.NavigationId != _previewNavigationId) return;
            var error = args.IsSuccess && args.HttpStatusCode < 400 ? null : $"Preview navigation failed: {args.WebErrorStatus}, HTTP {args.HttpStatusCode}.";
            if (error != null) NotePreviewDiagnostic("navigation-error", error);
            _previewNavigation?.TrySetResult(error);
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            NotePreviewDiagnostic("blocked-popup", args.Uri);
            if (_agentBrowserRequests == 0 && args.IsUserInitiated) ShellOpen.Try(args.Uri);
        };
        core.DownloadStarting += (_, args) =>
        {
            if (_agentBrowserRequests == 0) return;
            args.Cancel = true;
            NotePreviewDiagnostic("blocked-download", "Agent interaction attempted a download; no download was started.");
        };
        // Page alerts must not hang an agent turn behind a native modal dialog.
        core.Settings.AreDefaultScriptDialogsEnabled = _agentBrowserRequests == 0;
        core.ScriptDialogOpening += (_, args) =>
        {
            if (_agentBrowserRequests > 0) NotePreviewDiagnostic("script-dialog-dismissed", args.Message);
        };
        foreach (var eventName in new[] { "Runtime.consoleAPICalled", "Runtime.exceptionThrown", "Log.entryAdded", "Network.loadingFailed" })
        {
            var receiver = core.GetDevToolsProtocolEventReceiver(eventName);
            receiver.DevToolsProtocolEventReceived += (_, args) =>
            {
                if (!IsAllowedPreviewUrl(core.Source)) return;
                if (eventName == "Runtime.consoleAPICalled")
                {
                    using var message = JsonDocument.Parse(args.ParameterObjectAsJson);
                    var type = message.RootElement.GetProperty("type").GetString();
                    if (type is not ("error" or "warning" or "assert")) return;
                }
                NotePreviewDiagnostic(eventName, args.ParameterObjectAsJson);
            };
            _previewEventReceivers.Add(receiver);
        }
        foreach (var domain in new[] { "Runtime", "Log", "Network" })
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(domain + ".enable", "{}");
                _previewDiagnosticDomains[domain] = true;
            }
            catch (Exception ex)
            {
                _previewDiagnosticDomains[domain] = false;
                NotePreviewDiagnostic("diagnostics-unavailable", domain + ": " + ex.Message);
            }
        }
        // The preview must show the files as they are on disk. Without this, an edited stylesheet or
        // script can be served from cache on reload and the agent reviews the previous build.
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", """{"cacheDisabled":true}""");
            _previewCacheBypassed = true;
        }
        catch (Exception ex)
        {
            _previewCacheBypassed = false;
            NotePreviewDiagnostic("cache-bypass-unavailable", ex.Message);
        }
    }

    private async Task WaitForPreviewNavigationAsync(CancellationToken token)
    {
        if (_previewNavigation == null) return;
        var error = await _previewNavigation.Task.WaitAsync(token);
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
