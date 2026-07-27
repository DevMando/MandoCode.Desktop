using System.Net;
using System.Text;
using MandoCode.Models;
using Markdig;

namespace MandoCode.Desktop.Services;

/// <summary>
/// Builds the HTML fragments shown in the WebView2 transcript — the WinUI counterpart
/// of the CLI's MarkdownHtmlRenderer + OperationDisplayRenderer + diff panels, using
/// the same underlying models (Markdig markdown, OperationDisplayEvent, DiffLine).
/// </summary>
public sealed class TranscriptHtmlBuilder : ITranscriptHtml
{
    private readonly MandoCodeConfig _config;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()   // model output is untrusted — never let raw HTML through
        .Build();

    public TranscriptHtmlBuilder(MandoCodeConfig config) => _config = config;

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>
    /// Markdown → HTML with the same guard philosophy as the CLI's RenderMarkdownGuarded:
    /// build off-thread with a configurable budget; on timeout or failure fall back to
    /// escaped plain text so one pathological response can't hang the transcript.
    /// </summary>
    public string FromMarkdown(string markdown)
    {
        try
        {
            string? html = null;
            var buildTask = Task.Run(() => html = Markdown.ToHtml(markdown, Pipeline));
            var budget = TimeSpan.FromSeconds(Math.Max(1, _config.MarkdownRenderTimeoutSeconds));
            if (!buildTask.Wait(budget) || html == null)
            {
                return $"<pre class=\"raw\">{E(markdown)}</pre>" +
                       Dim($"(markdown rendering timed out after {_config.MarkdownRenderTimeoutSeconds}s — showing raw text)");
            }
            return html;
        }
        catch
        {
            return $"<pre class=\"raw\">{E(markdown)}</pre>";
        }
    }

    public string UserEcho(string text) =>
        $"<div class=\"user-echo\"><span class=\"ue-sigil\">&gt;</span> {E(text)}</div>";

    /// <summary>Null speaker keeps the classic MandoCode label — used by surfaces with no
    /// agent (the appearance preview). Per-agent callers pass the tab's name so the card
    /// agrees with what the system prompt told the model it's called.</summary>
    public string AssistantCard(string markdown, string? speaker = null) =>
        $"<div class=\"assistant\"><div class=\"assistant-label\">{E(speaker ?? "MandoCode")}</div><div class=\"md\">{FromMarkdown(markdown)}</div></div>";

    public string Info(string text) => $"<div class=\"line info\">{E(text)}</div>";
    public string Success(string text) => $"<div class=\"line success\">{E(text)}</div>";
    public string Warn(string text) => $"<div class=\"line warn\">{E(text)}</div>";
    public string Error(string text) => $"<div class=\"line error\">{E(text)}</div>";
    public string Dim(string text) => $"<div class=\"line dim\">{E(text)}</div>";

    /// <summary>True for blocks that describe LIVE session state (status chips: connection,
    /// model ready, MCP counts, pending offers) rather than conversation history. Session
    /// restore replays journaled transcripts — replaying a dead process's state pills next
    /// to the new session's real ones ("MCP connected" twice, stale "ready") reads as
    /// duplicate/conflicting status, so replay skips them. Journals still CONTAIN them:
    /// capture stays dumb and faithful; the judgment lives at replay.</summary>
    public static bool IsEphemeralStatus(string blockHtml) =>
        blockHtml.StartsWith("<div class=\"chip-row\"", StringComparison.Ordinal)
        // Boot/progress narration — true only while it was happening. ("Project root
        // changed to: X" is deliberately NOT here: that's a real event, kept as history.)
        || blockHtml.Contains(">Rebuilding the AI session for the new project…<", StringComparison.Ordinal)
        || blockHtml.Contains(">✓ Ready.<", StringComparison.Ordinal);

    /// <summary>A compact status pill — a colored state dot, a bold primary value, and an
    /// optional dim qualifier. The dot replaces status emoji: crisp and theme-aware.
    /// <paramref name="state"/> is "ok", "warn", "err", or "" (neutral).</summary>
    public string StatusChip(string primary, string? secondary = null, string state = "")
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"chip-row\"><span class=\"chip {state}\"><span class=\"dot\"></span>");
        sb.Append($"<span class=\"chip-val\">{E(primary)}</span>");
        if (!string.IsNullOrEmpty(secondary))
            sb.Append($"<span class=\"chip-key\">{E(secondary)}</span>");
        sb.Append("</span></div>");
        return sb.ToString();
    }

    /// <summary>
    /// A STATIC tool-call pill (no animation — draws once, costs nothing). Replaces the plain
    /// "[Function] …" / "[Done] ✓" text lines for non-file tools with something that reads as a
    /// distinct chip, in the same rounded, theme-colored, monochrome-glyph language as StatusChip.
    /// Deliberately not animated: an ever-spinning element pins the WebView compositor (see the
    /// reverted animated version). <paramref name="state"/> is "" (neutral), "done", or "err".
    /// </summary>
    /// <summary>
    /// A single STATIC tool-call pill: a neutral status dot + label (e.g. "Skill: deep-research"),
    /// in the MCP StatusChip family, centered. One pill per call — drawn once on invoke and NEVER
    /// updated afterward. Recoloring the dot in place on completion ("turn green when done")
    /// reproduced the CPU/stuck issue and was removed; the dot stays neutral.
    /// </summary>
    public string ToolChip(string label) =>
        $"<div class=\"tool-pill\"><span class=\"tp-dot\"></span>" +
        $"<span class=\"tp-label\">{E(label).Replace(".", " · ")}</span></div>";

    /// <summary>Pre-formatted block (config listings, model lists) in monospace.</summary>
    public string Mono(string text) => $"<pre class=\"mono-block\">{E(text)}</pre>";

    /// <summary>A clickable link line — opens in the default browser (see MainWindow's
    /// NavigationStarting handler, which redirects external navigation out of the WebView).</summary>
    public string Link(string text, string url) =>
        $"<div class=\"line\"><a href=\"{E(url)}\">{E(text)}</a></div>";

    /// <summary>
    /// A clickable file path — the desktop counterpart of the CLI's FileLinkHelper
    /// terminal hyperlinks. Clicking posts an "open-file:" web message that MainWindow
    /// resolves against the project root and opens with the default app.
    /// </summary>
    private static string FileLink(string path) =>
        $"<a class=\"file-link\" href=\"#\" data-file=\"{E(path)}\" title=\"Open in default app\">{E(path)}</a>";

    public string CommandCard(string command) =>
        $"<div class=\"panel\"><div class=\"panel-header sky\">Command</div><pre class=\"cmd\">$ {E(command)}</pre></div>";

    public string CommandOutputCard(string command, string output, bool failed = false) =>
        $"<div class=\"panel\"><div class=\"panel-header {(failed ? "red" : "sky")}\">$ {E(command)}</div><pre class=\"cmd-out\">{E(output)}</pre></div>";

    /// <summary><paramref name="interactive"/> adds Undo-changes / Clear chips to the header —
    /// used ONLY for diffs the user requested from the Changes tab, never for diffs the agent
    /// produces (those are a record of what happened, not an offer to act).</summary>
    public string DiffCard(string relativePath, IReadOnlyList<DiffLine> lines, string summary, bool interactive = false)
    {
        var sb = new StringBuilder();
        var actions = interactive
            ? $"<span class=\"dv-actions\"><button class=\"dv-btn dv-undo\" data-file=\"{E(relativePath)}\" " +
              "title=\"Discard this file's uncommitted changes (asks first)\">↩ Undo changes</button>" +
              "<button class=\"dv-btn dv-clear\" title=\"Remove this diff card from the transcript\">✕ Clear</button></span>"
            : "";
        sb.Append($"<div class=\"panel\"><div class=\"panel-header sky\">Diff: {FileLink(relativePath)}{actions}</div><pre class=\"diff\">");
        AppendDiffLines(sb, lines);
        sb.Append("</pre>");
        sb.Append($"<div class=\"panel-footer\">{E(summary)}</div></div>");
        return sb.ToString();
    }

    public string FolderDeleteCard(string relativePath, string listing) =>
        $"<div class=\"panel red-border\"><div class=\"panel-header red\">Delete Folder: {FileLink(relativePath)}/</div><pre class=\"cmd-out\">{E(listing)}</pre></div>";

    private static void AppendDiffLines(StringBuilder sb, IReadOnlyList<DiffLine> lines)
    {
        foreach (var line in lines)
        {
            switch (line.LineType)
            {
                case DiffLineType.Removed:
                    sb.Append($"<span class=\"d-rem\">{Num(line.OldLineNumber)} - {E(line.Content)}</span>\n");
                    break;
                case DiffLineType.Added:
                    sb.Append($"<span class=\"d-add\">{Num(line.NewLineNumber)} + {E(line.Content)}</span>\n");
                    break;
                default:
                    sb.Append($"<span class=\"d-ctx\">{Num(line.OldLineNumber)}   {E(line.Content)}</span>\n");
                    break;
            }
        }

        static string Num(int? n) => n.HasValue ? n.Value.ToString().PadLeft(4) : "    ";
    }

    /// <summary>
    /// Rich operation card — the WinUI counterpart of OperationDisplayRenderer.Render.
    /// </summary>
    public string OperationCard(OperationDisplayEvent op)
    {
        var (icon, cls) = op.OperationType switch
        {
            "Write" => ("✚", "success"),
            "Update" => ("✎", "sky"),
            "Read" => ("⊙", "dim"),
            "Delete" => ("✖", "error"),
            "CreateFolder" => ("▣", "success"),
            "Search" => ("⌕", "dim"),
            "List" => ("≡", "dim"),
            "Glob" => ("⌂", "dim"),
            "WebSearch" => ("◍", "dim"),
            "WebFetch" => ("↓", "dim"),
            "Command" => ("$", "sky"),
            _ => ("•", "dim")
        };

        // Paths open on click; op types whose "path" is really a query/command/URL don't.
        var pathIsOpenable = !string.IsNullOrEmpty(op.FilePath)
            && op.OperationType is "Write" or "Update" or "Read" or "Delete" or "CreateFolder" or "List";

        var sb = new StringBuilder();
        sb.Append("<div class=\"op\">");
        sb.Append($"<span class=\"op-head {cls}\">{icon} {E(op.OperationType)}</span> ");
        sb.Append(pathIsOpenable
            ? $"<span class=\"op-path\">{FileLink(op.FilePath!)}</span>"
            : $"<span class=\"op-path\">{E(op.FilePath)}</span>");

        var meta = new List<string>();
        if (op.LineCount > 0) meta.Add($"{op.LineCount} lines");
        if (op.Additions > 0) meta.Add($"+{op.Additions}");
        if (op.Deletions > 0) meta.Add($"-{op.Deletions}");
        if (op.IsNewFile) meta.Add("new file");
        if (meta.Count > 0)
            sb.Append($" <span class=\"op-meta\">({string.Join(", ", meta)})</span>");

        // Preview / inline diff — skipped when the approval flow already showed it.
        if (!op.ApprovalWasShown)
        {
            if (op.InlineDiff is { Count: > 0 })
            {
                sb.Append("<pre class=\"diff op-detail\">");
                AppendDiffLines(sb, op.InlineDiff);
                sb.Append("</pre>");
            }
            else if (!string.IsNullOrEmpty(op.ContentPreview))
            {
                // Web results are prose, not code — render them wrapped, in the reading font, dimmed,
                // so they recede as reference material instead of a highlighted code block. File
                // content previews (Read) stay monospace/no-wrap since they really are code.
                var prose = op.OperationType is "WebSearch" or "WebFetch";
                if (prose)
                {
                    // Web dumps are noisy reference material almost no one reads inline, so hide the
                    // preview behind an "Expand" chip on the op line. Expanding reveals the detail
                    // box, which carries its own "Collapse" button so it can be closed from the
                    // window too. Toggling is wired in the transcript's web-toggle click handler.
                    sb.Append("<button class=\"web-toggle\">⤢ Expand</button>");
                    sb.Append("<div class=\"web-detail\" hidden>");
                    sb.Append("<button class=\"expand-btn right web-collapse\" title=\"Collapse\">⤡ Collapse</button>");
                    sb.Append($"<pre class=\"cmd-out op-detail op-prose\">{E(op.ContentPreview)}");
                    if (op.RemainingLines > 0)
                        sb.Append($"\n<span class=\"dim\">… +{op.RemainingLines} more lines</span>");
                    sb.Append("</pre></div>");
                }
                else
                {
                    sb.Append($"<pre class=\"cmd-out op-detail\">{E(op.ContentPreview)}");
                    if (op.RemainingLines > 0)
                        sb.Append($"\n<span class=\"dim\">… +{op.RemainingLines} more lines</span>");
                    sb.Append("</pre>");
                }
            }
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    public string PlanCard(TaskPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"panel\"><div class=\"panel-header sky\">Proposed plan</div><table class=\"plan\">");
        sb.Append("<tr><th>Step</th><th>Description</th></tr>");
        foreach (var step in plan.Steps)
            sb.Append($"<tr><td class=\"sky\">{step.StepNumber}</td><td>{E(step.Description)}</td></tr>");
        sb.Append("</table></div>");
        return sb.ToString();
    }

    public string StepStarted(int current, int total, string description) =>
        $"<div class=\"line\"><span class=\"sky\">Step {current}/{total}:</span> {E(description)}</div>";

    public string TokenSummary(string text) =>
        $"<div class=\"line dim token-summary\">{E(text)}</div>";

    public string HelpCard(IEnumerable<(string Command, string Description)> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"panel\"><div class=\"panel-header sky\">Commands</div><table class=\"plan\">");
        foreach (var (cmd, desc) in rows)
            sb.Append($"<tr><td class=\"sky nowrap\">{E(cmd)}</td><td>{E(desc)}</td></tr>");
        sb.Append("</table></div>");
        return sb.ToString();
    }

    // The bulk of the transcript host page is static CSS and JS. It lives in
    // Assets/web/transcript/ (shipped by the Assets\web\** content glob), read once and injected
    // inline by BaseDocument below — so the page still renders in a single NavigateToString with no
    // extra fetch and no flash of unstyled content. Only the theme-dependent <html> flags and
    // :root variables remain in C#.
    private static readonly Lazy<string> TranscriptCss = new(() => ReadWebAsset("transcript.css"));
    private static readonly Lazy<string> TranscriptJs = new(() => ReadWebAsset("transcript.js"));

    private static string ReadWebAsset(string fileName) => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Assets", "web", "transcript", fileName));

    /// <summary>The transcript host page: the theme-dependent header and :root vars inline, with the
    /// bulk static CSS and JS injected from Assets/web/transcript/. Colors come from the active
    /// UiTheme; ThemeManager.BuildTranscriptScript re-points the same CSS variables at runtime.</summary>
    public static string BaseDocument(UiTheme theme) => $$"""
<!DOCTYPE html>
<html{{(theme.FlatMotion ? " data-flat=\"1\"" : "")}}{{(theme.Crt ? " data-crt=\"1\"" : "")}}{{(theme.Win98 ? " data-win98=\"1\"" : "")}}{{(ThemeManager.BoxedMessages ? " data-cards=\"1\"" : "")}}>
<head>
<meta charset="utf-8">
<script src="https://mandocode.assets/highlight.min.js"></script>
<style>
  :root {
    --bg: {{theme.Background}};
    --fg: {{theme.Text}};
    --dim: {{theme.Dim}};
    --accent: {{theme.Accent}};
    --gold: {{theme.Gold}};
    --sky: {{theme.Sky}};
    --green: {{theme.Green}};
    --red: {{theme.Red}};
    --panel: {{theme.Panel}};
    --border: {{theme.Border}};
    --diffadd: {{theme.DiffAdd}};
    --chat-bg-image: {{ThemeManager.ChatBackgroundCssValue()}};
    --chat-bg-opacity: {{ThemeManager.ChatBackgroundOpacityCss()}};
  }
{{TranscriptCss.Value}}
</style>
</head>
<body>
<!-- The e-ink image "shader": a self-contained SVG filter (no WebGL, no deps). Grayscale →
     contrast → subtract a tiled 8x8 Bayer threshold map → discretize to 1 bit. The result is
     black/white ordered dithering — the classic Kindle/newsprint halftone. sRGB interpolation
     keeps the threshold from drifting; alpha is forced opaque (the #bg element's own opacity
     still fades the picture). Referenced only by html[data-flat] #bg, above. -->
<svg width="0" height="0" style="position:absolute" aria-hidden="true"><defs>
  <filter id="eink" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="g"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="g" result="gc">
      <feFuncR type="linear" slope="1.35" intercept="-0.17"/>
      <feFuncG type="linear" slope="1.35" intercept="-0.17"/>
      <feFuncB type="linear" slope="1.35" intercept="-0.17"/>
    </feComponentTransfer>
    <feImage result="btile" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="btile" result="bayer"/>
    <feComposite in="gc" in2="bayer" operator="arithmetic" k1="0" k2="1" k3="-1" k4="0.5" result="d"/>
    <feComponentTransfer in="d">
      <feFuncR type="discrete" tableValues="0 1"/>
      <feFuncG type="discrete" tableValues="0 1"/>
      <feFuncB type="discrete" tableValues="0 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>
</defs></svg>
<div id="bg"></div>
<div id="log"></div>
<script>
{{TranscriptJs.Value}}
</script>
</body>
</html>
""";
}
