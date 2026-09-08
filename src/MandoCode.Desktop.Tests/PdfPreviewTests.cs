using System.Text.Json;
using MandoCode.Desktop.Services;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// PDFs are viewable by the user in the browser pane, but deliberately NOT openable by the agent:
/// a PDF's text and structure never reach the DOM, so an agent that opened one would be handed a
/// page that looks successfully loaded and reads as entirely empty. These pin that asymmetry.
/// </summary>
public sealed class PdfPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MandoPdfPreview-" + Guid.NewGuid().ToString("N"));
    public PdfPreviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private DesktopPreviewTools Tools() => new(new ProjectRootAccessor(_root)) { RequireTabId = true };
    private static bool Ok(string json) => JsonDocument.Parse(json).RootElement.GetProperty("ok").GetBoolean();

    [Fact]
    public async Task TheAgentCannotOpenAPdfAndIsToldWhichTypesWork()
    {
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "%PDF-1.4 not really a pdf");
        var tools = Tools();
        var dispatched = 0;
        tools.ExecuteAsync = (_, _) => { dispatched++; return Task.FromResult("{\"ok\":true}"); };

        var result = await tools.OpenDesktopPreview("report.pdf");

        Assert.False(Ok(result));
        Assert.Contains(".html", result);
        Assert.Equal(0, dispatched);   // refused before anything reached the browser
    }

    [Fact]
    public async Task TheSameCallStillWorksForAPageTheAgentCanActuallyRead()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html><p>hi");
        var tools = Tools();
        string? opened = null;
        tools.ExecuteAsync = (request, _) => { opened = request.FullPath; return Task.FromResult("{\"ok\":true}"); };

        await tools.OpenDesktopPreview("index.html");

        Assert.NotNull(opened);
        Assert.EndsWith("index.html", opened);
    }

    [Fact]
    public void InspectionReportsAPdfRatherThanReturningAnEmptyPage()
    {
        var script = DesktopPreviewScripts.Build(
            new("inspect", _root, Origin: "https://preview.mandocode.local"));

        // The guard must run for DOM reads, name the viewer as the reason, and say plainly that an
        // empty result is not evidence of an empty document.
        Assert.Contains("application/pdf", script);
        Assert.Contains("not evidence that the document is empty", script);
        // Screenshots are the supported way to judge a PDF, so their support operations stay exempt.
        Assert.Contains("args.operation !== 'pagestate'", script);
        Assert.Contains("screenshot_desktop_preview", script);
    }
}
