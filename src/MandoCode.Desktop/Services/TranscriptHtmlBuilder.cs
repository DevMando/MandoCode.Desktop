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
public sealed class TranscriptHtmlBuilder
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
        $"<div class=\"user-echo\">&gt; {E(text)}</div>";

    public string AssistantCard(string markdown) =>
        $"<div class=\"assistant\"><div class=\"assistant-label\">MandoCode</div><div class=\"md\">{FromMarkdown(markdown)}</div></div>";

    public string Info(string text) => $"<div class=\"line info\">{E(text)}</div>";
    public string Success(string text) => $"<div class=\"line success\">{E(text)}</div>";
    public string Warn(string text) => $"<div class=\"line warn\">{E(text)}</div>";
    public string Error(string text) => $"<div class=\"line error\">{E(text)}</div>";
    public string Dim(string text) => $"<div class=\"line dim\">{E(text)}</div>";

    /// <summary>Pre-formatted block (config listings, model lists) in monospace.</summary>
    public string Mono(string text) => $"<pre class=\"mono-block\">{E(text)}</pre>";

    /// <summary>A clickable link line — opens in the default browser (see MainWindow's
    /// NavigationStarting handler, which redirects external navigation out of the WebView).</summary>
    public string Link(string text, string url) =>
        $"<div class=\"line\"><a href=\"{E(url)}\">{E(text)}</a></div>";

    public string CommandCard(string command) =>
        $"<div class=\"panel\"><div class=\"panel-header sky\">Command</div><pre class=\"cmd\">$ {E(command)}</pre></div>";

    public string CommandOutputCard(string command, string output, bool failed = false) =>
        $"<div class=\"panel\"><div class=\"panel-header {(failed ? "red" : "sky")}\">$ {E(command)}</div><pre class=\"cmd-out\">{E(output)}</pre></div>";

    public string DiffCard(string relativePath, IReadOnlyList<DiffLine> lines, string summary)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"panel\"><div class=\"panel-header sky\">Diff: {E(relativePath)}</div><pre class=\"diff\">");
        AppendDiffLines(sb, lines);
        sb.Append("</pre>");
        sb.Append($"<div class=\"panel-footer\">{E(summary)}</div></div>");
        return sb.ToString();
    }

    public string FolderDeleteCard(string relativePath, string listing) =>
        $"<div class=\"panel red-border\"><div class=\"panel-header red\">Delete Folder: {E(relativePath)}/</div><pre class=\"cmd-out\">{E(listing)}</pre></div>";

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

        var sb = new StringBuilder();
        sb.Append("<div class=\"op\">");
        sb.Append($"<span class=\"op-head {cls}\">{icon} {E(op.OperationType)}</span> ");
        sb.Append($"<span class=\"op-path\">{E(op.FilePath)}</span>");

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
                sb.Append($"<pre class=\"cmd-out op-detail\">{E(op.ContentPreview)}");
                if (op.RemainingLines > 0)
                    sb.Append($"\n<span class=\"dim\">… +{op.RemainingLines} more lines</span>");
                sb.Append("</pre>");
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

    /// <summary>The transcript host page: styles + the append/clear JS the window calls.</summary>
    public static string BaseDocument() => """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  :root {
    --bg: #16121f;
    --fg: #e6e1f0;
    --dim: #8f87a3;
    --accent: #c864ff;
    --gold: #ffc850;
    --sky: #38b6ff;
    --green: #4ec94e;
    --red: #e05252;
    --panel: #201a2e;
    --border: #362c4d;
  }
  * { box-sizing: border-box; }
  body {
    background: var(--bg); color: var(--fg);
    font-family: "Segoe UI", sans-serif; font-size: 14px;
    margin: 0; padding: 14px 18px 24px 18px; line-height: 1.5;
  }
  #log > * { margin-bottom: 8px; }
  .user-echo { color: var(--gold); font-weight: 600; white-space: pre-wrap; margin-top: 14px; }
  .assistant { margin-top: 4px; }
  .assistant-label { color: var(--green); font-weight: 700; margin-bottom: 2px; }
  .md p { margin: 6px 0; }
  .md pre {
    background: var(--panel); border: 1px solid var(--border); border-radius: 8px;
    padding: 10px 12px; overflow-x: auto;
    font-family: "Cascadia Code", "Cascadia Mono", Consolas, monospace; font-size: 13px;
  }
  .md code { font-family: "Cascadia Code", Consolas, monospace; background: var(--panel);
    border-radius: 4px; padding: 1px 5px; font-size: 13px; }
  .md pre code { background: none; padding: 0; }
  .md table { border-collapse: collapse; margin: 8px 0; }
  .md th, .md td { border: 1px solid var(--border); padding: 4px 10px; }
  a { color: var(--sky); }
  .md h1, .md h2, .md h3 { color: var(--accent); margin: 12px 0 4px 0; }
  .md ul, .md ol { margin: 4px 0; padding-left: 24px; }
  .md blockquote { border-left: 3px solid var(--accent); margin: 6px 0; padding-left: 10px; color: var(--dim); }
  .line { white-space: pre-wrap; }
  .info { color: var(--sky); }
  .success { color: var(--green); }
  .warn { color: var(--gold); }
  .error { color: var(--red); }
  .dim { color: var(--dim); }
  .sky { color: var(--sky); }
  .red { color: var(--red); }
  .token-summary { text-align: right; font-size: 12px; }
  .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 10px;
    overflow: hidden; }
  .panel.red-border { border-color: var(--red); }
  .panel-header { padding: 6px 12px; font-weight: 600; border-bottom: 1px solid var(--border);
    font-family: "Cascadia Code", Consolas, monospace; font-size: 13px; }
  .panel-footer { padding: 4px 12px 8px 12px; color: var(--dim); font-size: 12px; }
  pre.cmd, pre.cmd-out, pre.diff, pre.mono-block, pre.raw {
    margin: 0; padding: 8px 12px; overflow-x: auto; white-space: pre;
    font-family: "Cascadia Code", "Cascadia Mono", Consolas, monospace; font-size: 13px;
  }
  pre.mono-block, pre.raw { background: var(--panel); border: 1px solid var(--border);
    border-radius: 8px; white-space: pre-wrap; }
  .d-add { color: #87cefa; display: block; }
  .d-rem { color: var(--red); display: block; }
  .d-ctx { color: var(--dim); display: block; }
  .op { margin: 2px 0; }
  .op-head { font-weight: 600; }
  .op-path { font-family: "Cascadia Code", Consolas, monospace; font-size: 13px; }
  .op-meta { color: var(--dim); font-size: 12px; }
  .op-detail { margin-top: 4px; background: var(--panel); border: 1px solid var(--border);
    border-radius: 8px; }
  table.plan { border-collapse: collapse; width: 100%; }
  table.plan th, table.plan td { border-top: 1px solid var(--border); padding: 5px 12px;
    text-align: left; vertical-align: top; }
  table.plan th { color: var(--dim); font-weight: 600; }
  .nowrap { white-space: nowrap; }
</style>
</head>
<body>
<div id="log"></div>
<script>
  const log = document.getElementById('log');
  window.__append = function (html) {
    const nearBottom = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 60);
    const wrap = document.createElement('div');
    wrap.innerHTML = html;
    while (wrap.firstChild) log.appendChild(wrap.firstChild);
    if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
  };
  window.__clear = function () { log.innerHTML = ''; };
</script>
</body>
</html>
""";
}
