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

    public string DiffCard(string relativePath, IReadOnlyList<DiffLine> lines, string summary)
    {
        var sb = new StringBuilder();
        sb.Append($"<div class=\"panel\"><div class=\"panel-header sky\">Diff: {FileLink(relativePath)}</div><pre class=\"diff\">");
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
                var detailCls = prose ? "cmd-out op-detail op-prose" : "cmd-out op-detail";
                sb.Append($"<pre class=\"{detailCls}\">{E(op.ContentPreview)}");
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

    /// <summary>The transcript host page: styles + the append/clear JS the window calls.
    /// Colors come from the active UiTheme; ThemeManager.BuildTranscriptScript re-points
    /// the same CSS variables when the theme changes at runtime.</summary>
    public static string BaseDocument(UiTheme theme) => $$"""
<!DOCTYPE html>
<html>
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
  }
  * { box-sizing: border-box; }
  body {
    background: var(--bg); color: var(--fg);
    font-family: "Segoe UI", sans-serif; font-size: 14px;
    margin: 0; padding: 14px 18px 24px 18px; line-height: 1.5;
  }
  #log > * { margin-bottom: 8px; animation: rise 0.18s ease-out; }
  @keyframes rise {
    from { opacity: 0; transform: translateY(4px); }
    to { opacity: 1; transform: none; }
  }
  .user-echo { color: var(--gold); font-weight: 600; white-space: pre-wrap; margin-top: 14px; }
  .assistant { margin-top: 4px; position: relative; }
  .assistant-label { color: var(--green); font-weight: 700; margin-bottom: 2px; }
  .md p { margin: 6px 0; }
  .md pre {
    background: var(--panel); border: 1px solid var(--border); border-radius: 8px;
    padding: 10px 12px; overflow-x: auto; position: relative;
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

  /* Status chips — compact pills for session/connection state. A CSS status dot
     (crisp, theme-aware) replaces status emoji; state = ok | warn | err | neutral. */
  /* Centered to match the tool pills: all system/status chrome sits centered, conversation stays left. */
  .chip-row { margin: 2px 0; text-align: center; }
  .chip { display: inline-flex; align-items: center; gap: 7px;
    padding: 3px 12px; border-radius: 999px; font-size: 12.5px;
    border: 1px solid var(--border); background: var(--panel);
    /* Uniform floor so status pills line up — the "MCP / N connected" pill is the
       widest of them, so shorter pills (model / ready) pad up to match. Longer
       chips still grow past it. */
    box-sizing: border-box; min-width: 190px; }
  .chip .dot { width: 7px; height: 7px; border-radius: 50%; flex: none;
    background: var(--dim); box-shadow: 0 0 0 3px color-mix(in srgb, var(--dim) 20%, transparent); }
  .chip.ok .dot { background: var(--green);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--green) 24%, transparent); }
  .chip.warn .dot { background: var(--gold);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--gold) 24%, transparent); }
  .chip.err .dot { background: var(--red);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--red) 24%, transparent); }
  .chip-val { color: var(--fg); font-weight: 600; }
  .chip-key { color: var(--dim); }

  /* Tool-call pills — STATIC (no animation, so they never cause continuous repaint). A rounded,
     theme-colored chip with a monochrome glyph, matching the StatusChip family. */
  /* Centered: tool pills are the assistant's machinery, not dialogue — centering (like Slack/Discord
     system messages) keeps the left column a clean read and marks them as ambient activity.
     display:flex + fit-content makes the chip block-level and shrink-wrapped so margin auto centers it. */
  .tool-pill { display: flex; width: fit-content; align-items: center; gap: 8px; margin: 2px auto;
    padding: 3px 12px; border-radius: 999px; font-size: 12px;
    border: 1px solid var(--border); background: var(--panel); }
  .tool-pill .tp-dot { width: 7px; height: 7px; border-radius: 50%; flex: none;
    background: var(--dim);
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--dim) 20%, transparent); }
  .tool-pill .tp-label { color: var(--fg);
    font-family: "Cascadia Code", Consolas, monospace; font-size: 12px; }
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
  .d-add { color: var(--diffadd); display: block; }
  .d-rem { color: var(--red); display: block; }
  .d-ctx { color: var(--dim); display: block; }
  a.file-link { color: var(--sky); text-decoration: none;
    border-bottom: 1px dotted color-mix(in srgb, var(--sky) 55%, transparent); cursor: pointer; }
  a.file-link:hover { color: var(--accent); border-bottom-color: var(--accent); }
  .op { margin: 2px 0; }
  .op-head { font-weight: 600; }
  .op-path { font-family: "Cascadia Code", Consolas, monospace; font-size: 13px; }
  .op-meta { color: var(--dim); font-size: 12px; }
  .op-detail { margin-top: 4px; background: var(--panel); border: 1px solid var(--border);
    border-radius: 8px; }
  /* Prose tool output (web search/fetch): wrap to width, reading font, dimmed — reference material,
     not a code block. Declared after pre.cmd-out so these win on shared properties. */
  pre.op-prose { white-space: pre-wrap; word-break: break-word; overflow-x: hidden;
    font-family: "Segoe UI", sans-serif; font-size: 12.5px; color: var(--dim); }
  table.plan { border-collapse: collapse; width: 100%; }
  table.plan th, table.plan td { border-top: 1px solid var(--border); padding: 5px 12px;
    text-align: left; vertical-align: top; }
  table.plan th { color: var(--dim); font-weight: 600; }
  .nowrap { white-space: nowrap; }

  /* Syntax highlighting: highlight.js token classes mapped onto the theme's CSS
     variables, so code colors follow every theme (and survive live retheming). */
  .hljs { background: transparent; color: var(--fg); }
  .hljs-comment, .hljs-quote { color: var(--dim); font-style: italic; }
  .hljs-keyword, .hljs-selector-tag, .hljs-literal, .hljs-doctag { color: var(--accent); }
  .hljs-string, .hljs-regexp, .hljs-addition { color: var(--green); }
  .hljs-number, .hljs-symbol, .hljs-bullet, .hljs-meta, .hljs-built_in { color: var(--gold); }
  .hljs-title, .hljs-section, .hljs-name, .hljs-title.function_, .hljs-title.class_ { color: var(--sky); }
  .hljs-attr, .hljs-attribute, .hljs-variable, .hljs-template-variable, .hljs-type { color: var(--sky); }
  .hljs-deletion { color: var(--red); }
  .hljs-emphasis { font-style: italic; }
  .hljs-strong { font-weight: bold; }

  /* Copy chips — appear on hover over code blocks and assistant messages. Label is
     CSS generated content so it never pollutes the copied innerText. */
  .copy-chip { position: absolute; top: 6px; right: 6px; z-index: 1; opacity: 0;
    transition: opacity 0.12s; background: var(--bg); color: var(--dim);
    border: 1px solid var(--border); border-radius: 6px; padding: 2px 9px;
    font-size: 11px; font-family: "Segoe UI", sans-serif; cursor: pointer; }
  .copy-chip::before { content: "Copy"; }
  .copy-chip.copied::before { content: "Copied ✓"; }
  .copy-chip.copied { color: var(--green); border-color: var(--green); }
  .md pre:hover .copy-chip, .assistant:hover > .copy-chip { opacity: 1; }
  .copy-chip:hover { color: var(--fg); border-color: var(--accent); }

  /* Reactions, Teams-style. A ghosted add-reaction button fades in on hover next to the
     copy chip; clicking it opens a floating picker card (mirrors the input box's emoji
     flyout). Chosen reactions sit under the message as pills — no space is reserved
     until one exists. Delivery to the model: ChatController.SubmitAsync. */
  .react-ghost { position: absolute; top: 6px; right: 56px; z-index: 1; opacity: 0;
    transition: opacity 0.12s; background: var(--bg); color: var(--dim);
    border: 1px solid var(--border); border-radius: 6px; padding: 2px 8px;
    font-size: 12px; cursor: pointer;
    font-family: "Segoe UI Emoji", "Segoe UI", sans-serif; }
  .assistant:hover > .react-ghost { opacity: 0.55; }
  .react-ghost:hover { opacity: 1 !important; border-color: var(--accent); color: var(--fg); }
  /* The copy chip widens to "Copied ✓" for ~1.4s after a click; the ghost sits close
     enough to collide, so it ducks out for the duration of the flash. */
  .copy-chip.copied ~ .react-ghost { opacity: 0 !important; pointer-events: none; }
  #rx-pop { position: absolute; z-index: 50; display: none; width: 316px;
    background: var(--panel); border: 1px solid var(--border); border-radius: 10px;
    padding: 8px; box-shadow: 0 6px 24px rgba(0,0,0,0.45); }
  #rx-pop .rx { background: none; border: 1px solid transparent; border-radius: 6px;
    padding: 2px 5px; font-size: 17px; line-height: 22px; cursor: pointer;
    font-family: "Segoe UI Emoji", "Segoe UI", sans-serif; }
  #rx-pop .rx:hover { background: var(--bg); border-color: var(--border); }
  #rx-pop .rx.on { background: var(--bg); border-color: var(--accent); }
  /* flex-wrap is the safety net: if emoji glyphs render wider than budgeted (font
     version varies by Windows build), the row wraps inside the card instead of
     bleeding past its border. */
  #rx-pop .rx-quick { display: flex; flex-wrap: wrap; gap: 2px; align-items: center; }
  #rx-pop .rx-more-btn { margin-left: auto; background: none; border: none;
    color: var(--dim); font-size: 12px; cursor: pointer; padding: 2px 6px; }
  #rx-pop .rx-more-btn:hover { color: var(--fg); }
  #rx-pop .rx-grid { display: none; flex-wrap: wrap; gap: 2px; margin-top: 6px;
    padding-top: 6px; border-top: 1px solid var(--border); max-height: 156px;
    overflow-y: auto; }
  .rx-tray { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 6px; }
  .rx-pill { background: var(--panel); border: 1px solid var(--accent); border-radius: 999px;
    padding: 1px 9px; font-size: 13px; line-height: 19px; cursor: pointer;
    font-family: "Segoe UI Emoji", "Segoe UI", sans-serif; }
  .rx-pill:hover { border-color: var(--dim); opacity: 0.85; }

  /* Consecutive operation cards group into a collapsible run; it stays open while
     the run is active and collapses once a non-operation block lands after it. */
  details.op-group { margin: 2px 0; }
  details.op-group summary { color: var(--dim); font-size: 12px; cursor: pointer; user-select: none; }
  details.op-group summary:hover { color: var(--fg); }
  details.op-group > .op { margin-left: 16px; }

  /* Jump-to-bottom pill — shows when scrolled away from the live end of the chat. */
  #jump-pill { position: fixed; bottom: 14px; left: 50%; transform: translateX(-50%);
    display: none; z-index: 40; background: var(--panel); color: var(--fg);
    border: 1px solid var(--accent); border-radius: 999px; padding: 6px 14px;
    font-size: 12px; cursor: pointer; box-shadow: 0 4px 16px rgba(0,0,0,0.4); }

  /* In-chat find bar (Ctrl+F while the transcript has focus). */
  #findbar { position: fixed; top: 10px; right: 16px; z-index: 60; display: none;
    align-items: center; gap: 6px; background: var(--panel);
    border: 1px solid var(--border); border-radius: 8px; padding: 6px 8px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.4); }
  #findbar input { background: var(--bg); color: var(--fg); border: 1px solid var(--border);
    border-radius: 6px; padding: 3px 8px; font-size: 12px; width: 180px; outline: none; }
  #findbar .find-count { color: var(--dim); font-size: 11px; min-width: 44px; text-align: center; }
  #findbar button { background: none; border: none; color: var(--dim); cursor: pointer;
    font-size: 12px; padding: 2px 6px; }
  #findbar button:hover { color: var(--fg); }
  mark.find-hit { background: var(--gold); color: #000; border-radius: 2px; }
  mark.find-hit.find-current { background: var(--accent); color: #fff; }

  /* Scrollbars — Chromium's stock chrome ignores the theme; restyle every scroll surface
     (page, code blocks, reaction picker grid) to match it. */
  ::-webkit-scrollbar { width: 10px; height: 10px; }
  ::-webkit-scrollbar-track { background: transparent; }
  ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 5px;
    border: 2px solid transparent; background-clip: padding-box; }
  ::-webkit-scrollbar-thumb:hover { background-color: var(--dim); }
  ::-webkit-scrollbar-corner { background: transparent; }
  #rx-pop .rx-grid::-webkit-scrollbar { width: 7px; }
</style>
</head>
<body>
<div id="log"></div>
<script>
  const log = document.getElementById('log');
  if (window.hljs) hljs.configure({ ignoreUnescapedHTML: true });

  // --- append pipeline: group consecutive op cards, stamp timestamps ---
  function groupSummary(d) {
    const n = d.querySelectorAll(':scope > .op').length;
    d.querySelector('summary').textContent = '⚙ ' + n + ' operation' + (n === 1 ? '' : 's');
  }
  function placeChild(c) {
    if (c.nodeType !== 1) { log.appendChild(c); return; }
    if (c.classList.contains('op')) {
      const prev = log.lastElementChild;
      if (prev && prev.tagName === 'DETAILS' && prev.classList.contains('op-group') && prev.hasAttribute('open')) {
        prev.appendChild(c);
        groupSummary(prev);
        return;
      }
      if (prev && prev.classList.contains('op')) {
        const d = document.createElement('details');
        d.className = 'op-group';
        d.setAttribute('open', '');
        d.appendChild(document.createElement('summary'));
        log.insertBefore(d, prev);
        d.appendChild(prev);
        d.appendChild(c);
        groupSummary(d);
        return;
      }
      log.appendChild(c);
      return;
    }
    const last = log.lastElementChild;
    if (last && last.tagName === 'DETAILS' && last.classList.contains('op-group'))
      last.removeAttribute('open');   // run over — collapse the group
    if (c.classList.contains('assistant') || c.classList.contains('user-echo'))
      c.title = new Date().toLocaleTimeString();
    log.appendChild(c);
  }

  // --- syntax highlighting + copy chips, applied to new nodes only ---
  function highlightNew() {
    if (!window.hljs) return;
    log.querySelectorAll('.md pre code:not([data-hl])').forEach(function (c) {
      c.setAttribute('data-hl', '1');
      try { hljs.highlightElement(c); } catch (err) { }
    });
  }
  function doCopy(text, chip) {
    window.chrome.webview.postMessage('copy:' + text);
    chip.classList.add('copied');
    setTimeout(function () { chip.classList.remove('copied'); }, 1400);
  }
  function addCopyChips() {
    log.querySelectorAll('.md pre:not([data-copy])').forEach(function (pre) {
      pre.setAttribute('data-copy', '1');
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      chip.addEventListener('click', function (ev) {
        ev.stopPropagation();
        const code = pre.querySelector('code');
        doCopy(code ? code.innerText : pre.innerText, chip);
      });
      pre.appendChild(chip);
    });
    log.querySelectorAll('.assistant:not([data-copy])').forEach(function (card) {
      card.setAttribute('data-copy', '1');
      const md = card.querySelector('.md');
      if (!md) return;
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      chip.addEventListener('click', function (ev) {
        ev.stopPropagation();
        doCopy(md.innerText, chip);
      });
      card.appendChild(chip);
    });
  }

  // --- emoji reactions on assistant responses: hover ghost → picker card → pills ---
  // Toggling posts react:/unreact: with a JSON payload; the snippet lets the preamble
  // on the user's next turn say WHICH response was reacted to.
  const RX_QUICK = ['👍', '👎', '❤️', '🔥', '🎉', '🤔', '😂'];
  const RX_MORE = ['😀', '😄', '😊', '😉', '😍', '🥰', '😎', '🤓', '🙃', '😅', '😬', '😭',
    '🥳', '🤯', '😴', '🙄', '😤', '😱', '🫠', '🤗', '🫡', '👌', '🙏', '👏', '💪', '🤝',
    '✌️', '🤞', '👀', '🧠', '💯', '✨', '🚀', '🎯', '💡', '⚡', '⭐', '💔', '✅', '❌',
    '⚠️', '❓', '❗', '💬', '🐛', '🔧', '🔒', '🔑', '📝', '📌', '📁', '🖥️', '☕', '🍕',
    '🎮', '🤖'];
  let rxSeq = 0;
  let rxCard = null;   // the card the open picker targets

  const rxPop = document.createElement('div');
  rxPop.id = 'rx-pop';
  document.body.appendChild(rxPop);

  function rxSnippet(card) {
    const md = card.querySelector('.md');
    return (md ? md.innerText : '').trim().replace(/\s+/g, ' ').slice(0, 80);
  }
  function rxPillFor(card, emoji) {
    const tray = card.querySelector('.rx-tray');
    if (!tray) return null;
    return Array.prototype.find.call(tray.children, function (p) { return p.textContent === emoji; });
  }
  // Toggle a reaction on a card: pill tray + picker highlight + postMessage, all in one place.
  function rxToggle(card, emoji) {
    const existing = rxPillFor(card, emoji);
    if (existing) {
      existing.remove();
      const tray = card.querySelector('.rx-tray');
      if (tray && !tray.children.length) tray.remove();
      window.chrome.webview.postMessage('unreact:' +
        JSON.stringify({ id: card.dataset.rxId, emoji: emoji, snippet: '' }));
    } else {
      let tray = card.querySelector('.rx-tray');
      if (!tray) {
        tray = document.createElement('div');
        tray.className = 'rx-tray';
        card.appendChild(tray);
      }
      const pill = document.createElement('button');
      pill.className = 'rx-pill';
      pill.textContent = emoji;
      pill.title = 'Click to remove reaction';
      pill.addEventListener('click', function (ev) {
        ev.stopPropagation();
        rxToggle(card, emoji);
      });
      tray.appendChild(pill);
      window.chrome.webview.postMessage('react:' +
        JSON.stringify({ id: card.dataset.rxId, emoji: emoji, snippet: rxSnippet(card) }));
    }
    // Picking (or un-picking) from the open picker dismisses it — one-shot action,
    // like Teams/Slack. Multiple reactions = reopen; chosen ones show highlighted.
    if (rxPop.style.display === 'block' && rxCard === card) closeRxPop();
  }
  function rxChip(parent, emoji) {
    const b = document.createElement('button');
    b.className = 'rx' + (rxCard && rxPillFor(rxCard, emoji) ? ' on' : '');
    b.textContent = emoji;
    b.addEventListener('click', function (ev) {
      ev.stopPropagation();
      rxToggle(rxCard, emoji);
    });
    parent.appendChild(b);
  }
  function openRxPop(card, anchor) {
    rxCard = card;
    rxPop.innerHTML = '';
    const quick = document.createElement('div');
    quick.className = 'rx-quick';
    RX_QUICK.forEach(function (e) { rxChip(quick, e); });
    const moreBtn = document.createElement('button');
    moreBtn.className = 'rx-more-btn';
    moreBtn.textContent = 'More ▾';
    quick.appendChild(moreBtn);
    rxPop.appendChild(quick);
    const grid = document.createElement('div');
    grid.className = 'rx-grid';
    RX_MORE.forEach(function (e) { rxChip(grid, e); });
    rxPop.appendChild(grid);
    moreBtn.addEventListener('click', function (ev) {
      ev.stopPropagation();
      const opening = grid.style.display !== 'flex';
      grid.style.display = opening ? 'flex' : 'none';
      moreBtn.textContent = opening ? 'Less ▴' : 'More ▾';
    });
    // Anchor under the ghost button, right-aligned; flip above when near the viewport bottom.
    rxPop.style.display = 'block';
    const r = anchor.getBoundingClientRect();
    const pw = rxPop.offsetWidth, ph = rxPop.offsetHeight;
    const left = Math.max(8, Math.min(r.right - pw, window.innerWidth - pw - 8)) + window.scrollX;
    let top = r.bottom + 6 + window.scrollY;
    if (r.bottom + ph + 12 > window.innerHeight) top = Math.max(window.scrollY + 8, r.top - ph - 6 + window.scrollY);
    rxPop.style.left = left + 'px';
    rxPop.style.top = top + 'px';
  }
  function closeRxPop() { rxPop.style.display = 'none'; rxCard = null; }
  document.addEventListener('click', function (e) {
    if (rxPop.style.display === 'block' && !rxPop.contains(e.target)) closeRxPop();
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') closeRxPop();
  });

  function addReactionGhosts() {
    log.querySelectorAll('.assistant:not([data-rx])').forEach(function (card) {
      card.setAttribute('data-rx', '1');
      card.dataset.rxId = String(++rxSeq);
      const ghost = document.createElement('button');
      ghost.className = 'react-ghost';
      ghost.textContent = '🙂+';
      ghost.title = 'React to this response';
      ghost.addEventListener('click', function (ev) {
        ev.stopPropagation();
        if (rxPop.style.display === 'block' && rxCard === card) { closeRxPop(); return; }
        openRxPop(card, ghost);
      });
      card.appendChild(ghost);
    });
  }

  window.__append = function (html) {
    const nearBottom = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 60);
    const wrap = document.createElement('div');
    wrap.innerHTML = html;
    while (wrap.firstChild) placeChild(wrap.firstChild);
    highlightNew();
    addCopyChips();
    addReactionGhosts();
    if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
    updatePill();
  };
  window.__clear = function () { log.innerHTML = ''; updatePill(); };

  document.addEventListener('click', function (e) {
    const link = e.target.closest('a[data-file]');
    if (!link) return;
    e.preventDefault();
    window.chrome.webview.postMessage('open-file:' + link.getAttribute('data-file'));
  });

  // --- jump-to-bottom pill ---
  const pill = document.createElement('div');
  pill.id = 'jump-pill';
  pill.textContent = '↓ Latest';
  document.body.appendChild(pill);
  pill.addEventListener('click', function () {
    window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
  });
  function updatePill() {
    const nb = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 80);
    pill.style.display = nb ? 'none' : 'block';
  }
  window.addEventListener('scroll', updatePill);

  // --- in-chat find (Ctrl+F) ---
  let findBar = null, findHits = [], findIdx = -1;
  function ensureFindBar() {
    if (findBar) return;
    findBar = document.createElement('div');
    findBar.id = 'findbar';
    findBar.innerHTML = '<input type="text" placeholder="Find in chat"/><span class="find-count"></span>' +
      '<button data-act="prev">▲</button><button data-act="next">▼</button><button data-act="close">✕</button>';
    document.body.appendChild(findBar);
    const input = findBar.querySelector('input');
    let deb = null;
    input.addEventListener('input', function () {
      clearTimeout(deb);
      deb = setTimeout(function () { runFind(input.value); }, 150);
    });
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); stepFind(e.shiftKey ? -1 : 1); }
    });
    findBar.addEventListener('click', function (e) {
      const b = e.target.closest('button');
      if (!b) return;
      if (b.dataset.act === 'prev') stepFind(-1);
      else if (b.dataset.act === 'next') stepFind(1);
      else closeFind();
    });
  }
  function setCount() {
    if (!findBar) return;
    findBar.querySelector('.find-count').textContent = findHits.length ? (findIdx + 1) + '/' + findHits.length : '';
  }
  function clearFind() {
    findHits.forEach(function (m) {
      const p = m.parentNode;
      if (!p) return;
      p.replaceChild(document.createTextNode(m.textContent), m);
      p.normalize();
    });
    findHits = [];
    findIdx = -1;
    setCount();
  }
  function runFind(q) {
    clearFind();
    if (!q || q.length < 2) return;
    const needle = q.toLowerCase();
    const walker = document.createTreeWalker(log, NodeFilter.SHOW_TEXT, null);
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) {
      if (n.textContent.toLowerCase().includes(needle)) nodes.push(n);
    }
    nodes.forEach(function (node) {
      let text = node, idx;
      while ((idx = text.textContent.toLowerCase().indexOf(needle)) >= 0) {
        const hit = text.splitText(idx);
        const rest = hit.splitText(q.length);
        const m = document.createElement('mark');
        m.className = 'find-hit';
        hit.parentNode.replaceChild(m, hit);
        m.appendChild(hit);
        findHits.push(m);
        text = rest;
      }
    });
    if (findHits.length) { findIdx = 0; focusHit(); }
    setCount();
  }
  function stepFind(dir) {
    if (!findHits.length) return;
    findHits[findIdx].classList.remove('find-current');
    findIdx = (findIdx + dir + findHits.length) % findHits.length;
    focusHit();
    setCount();
  }
  function focusHit() {
    const m = findHits[findIdx];
    m.classList.add('find-current');
    m.scrollIntoView({ block: 'center' });
  }
  function closeFind() {
    clearFind();
    if (findBar) findBar.style.display = 'none';
  }
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'f' || e.key === 'F')) {
      e.preventDefault();
      ensureFindBar();
      findBar.style.display = 'flex';
      const input = findBar.querySelector('input');
      input.focus();
      input.select();
    }
    else if (e.key === 'Escape' && findBar && findBar.style.display !== 'none') {
      closeFind();
    }
  });
</script>
</body>
</html>
""";
}
