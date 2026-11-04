using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MandoCode.Desktop.Services;

/// <summary>UI-thread-owned frame document identities for one WebView. Never resolves via selection.</summary>
public sealed class BrowserFrames
{
    private sealed class Entry(CoreWebView2Frame frame, Entry? parent)
    {
        public CoreWebView2Frame Frame { get; } = frame;
        public Entry? Parent { get; } = parent;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? Url { get; set; }
        public string State { get; set; } = "loading";
        public bool Dead { get; set; }
    }

    private readonly List<Entry> _frames = [];

    public BrowserFrames(CoreWebView2 core)
    {
        core.FrameCreated += (_, e) => Track(e.Frame, null);
        // A cancelled navigation never replaces the document, so its frames stay live. The host
        // registers this after its own blocking handler, so Cancel is already decided here.
        core.NavigationStarting += (_, e) =>
        {
            if (e.Cancel) return;
            foreach (var entry in _frames) entry.Dead = true;
            _frames.Clear();
        };
    }

    private void Track(CoreWebView2Frame frame, Entry? parent)
    {
        var entry = new Entry(frame, parent);
        _frames.Add(entry);
        frame.FrameCreated += (_, e) => Track(e.Frame, entry);
        frame.Destroyed += (_, _) => { entry.Dead = true; _frames.Remove(entry); };
        frame.NavigationStarting += (_, e) =>
        {
            foreach (var child in _frames.Where(candidate => IsDescendant(candidate, entry)).ToArray())
            {
                child.Dead = true;
                _frames.Remove(child);
            }
            entry.Id = Guid.NewGuid().ToString("N");
            entry.Url = e.Uri;
            entry.State = "loading";
        };
        frame.NavigationCompleted += (_, e) => entry.State = e.IsSuccess ? "ready" : "navigation-failed";
    }

    private static bool Alive(Entry entry) => !entry.Dead && (entry.Parent == null || Alive(entry.Parent));

    private static bool IsDescendant(Entry candidate, Entry parent)
    {
        for (var ancestor = candidate.Parent; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor == parent) return true;
        return false;
    }

    public JsonArray Describe() => new(_frames.Where(Alive).Take(100).Select(entry => (JsonNode)new JsonObject
    {
        ["frameId"] = entry.Id, ["parentFrameId"] = entry.Parent?.Id ?? "main",
        ["url"] = entry.Url, ["state"] = entry.State,
        ["inspection"] = "Not inspected by this result. Use inspect_desktop_preview with this frameId."
    }).ToArray());

    public async Task<JsonObject> ExecuteAsync(DesktopPreviewRequest request, CancellationToken token)
    {
        var entry = _frames.FirstOrDefault(e => Alive(e) && e.Id == request.FrameId);
        if (entry == null) throw new InvalidOperationException("Frame is closed, navigated, or unknown. List frames again; no other document was used.");
        var id = entry.Id;
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (!Alive(entry) || entry.Id != id)
                throw new InvalidOperationException("The targeted frame changed during the operation. No other frame was used; a dispatched action may have occurred.");
        }
        Check();
        if (entry.State != "ready") throw new InvalidOperationException("The frame is loading or navigation failed. Its contents have not been inspected.");
        // Read the actual document origin, including about:blank/srcdoc inherited origins.
        var originJson = await entry.Frame.ExecuteScriptAsync("JSON.stringify({origin:location.origin})");
        Check();
        var originText = System.Text.Json.JsonSerializer.Deserialize<string>(originJson);
        var origin = originText == null ? null : JsonNode.Parse(originText)?["origin"]?.GetValue<string>();
        if (origin == null) throw new InvalidOperationException("The frame document could not be accessed. Do not infer that its form is absent.");
        var json = await entry.Frame.ExecuteScriptAsync(DesktopPreviewScripts.Build(request with { Origin = origin }));
        Check();
        var state = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("The frame did not return DOM evidence. Its content may be unavailable, not empty.");
        state["frameId"] = id;
        return state;
    }
}
