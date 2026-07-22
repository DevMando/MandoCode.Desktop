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
        $"<div class=\"user-echo\"><span class=\"ue-sigil\">&gt;</span> {E(text)}</div>";

    public string AssistantCard(string markdown) =>
        $"<div class=\"assistant\"><div class=\"assistant-label\">MandoCode</div><div class=\"md\">{FromMarkdown(markdown)}</div></div>";

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

    /// <summary>The transcript host page: styles + the append/clear JS the window calls.
    /// Colors come from the active UiTheme; ThemeManager.BuildTranscriptScript re-points
    /// the same CSS variables when the theme changes at runtime.</summary>
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
  * { box-sizing: border-box; }
  body {
    background: var(--bg); color: var(--fg);
    font-family: "Segoe UI", sans-serif; font-size: 14px;
    margin: 0; padding: 14px 18px 24px 18px; line-height: 1.5;
  }
  /* User-chosen chat background: a fixed full-bleed layer painted behind the log.
     Only THIS layer fades with the appearance slider — text keeps full contrast,
     and panels/code blocks keep their opaque theme backgrounds on top of it. */
  #bg { position: fixed; inset: 0; z-index: -1; pointer-events: none;
    background-image: var(--chat-bg-image); background-size: cover;
    background-position: center; background-repeat: no-repeat;
    opacity: var(--chat-bg-opacity); }
  #log > * { margin-bottom: 8px; animation: rise 0.18s ease-out; }
  @keyframes rise {
    from { opacity: 0; transform: translateY(4px); }
    to { opacity: 1; transform: none; }
  }
  /* E-ink / flat-motion themes: no fade-in, no hover transitions, no smooth scroll — the
     transcript repaints instantly and stays still, the way an e-reader page does. The
     attribute is set at build time and toggled live by ThemeManager.BuildTranscriptScript. */
  html[data-flat] #log > * { animation: none; }
  html[data-flat] *, html[data-flat] { transition: none !important; scroll-behavior: auto !important; }
  /* E-ink background image: treat the (static) chat-background layer like a Kindle image —
     grayscale + contrast + 1-bit Bayer ordered dithering into black/white halftone dots.
     Applied ONLY to #bg (never the text) and ONLY under the flat/e-ink theme. The layer is
     fixed and repaints once, so even this heavy filter costs nothing per frame. */
  html[data-flat] #bg { filter: url(#eink); }
  /* Color emoji is the loudest break in the paper illusion, so desaturate every emoji-bearing
     surface to grayscale ink: the chrome (react ghost, reaction pills, picker) AND the inline
     emoji in message text (.md) and user echoes. Scoped to the flat/e-ink theme only. Safe and
     static — assistant turns are appended as COMPLETE blocks (ChatController flushes each turn
     via AssistantCard; no token-by-token DOM streaming), so each subtree is filtered once on
     append and never re-rasterized by later appends. Under e-ink every other glyph is already
     ink-gray, so the only visible effect is draining the color out of emoji. */
  html[data-flat] .react-ghost,
  html[data-flat] .rx-pill,
  html[data-flat] #rx-pop .rx,
  html[data-flat] .md,
  html[data-flat] .user-echo { filter: grayscale(1); }

  /* ---- CRT picture-tube overlay (aperture-grille tube) ----------------------------------
     Scoped to html[data-crt]. Drawn on two fixed, pointer-events:none pseudo-layers OVER the
     transcript, so the "glass" sits in front of the text. EVERYTHING here is STATIC — no moving
     scanline, no flicker (that is the continuous-repaint trap we keep avoiding); the tube look
     is fixed gradients only, one paint. The set's native chrome outside the WebView is untouched,
     exactly like a real TV where only the picture tube carries scanlines. */
  html[data-crt] body {
    /* phosphor bloom on every glyph — a tight bright core + a wider soft halo reads more
       like real phosphor than one big blur (and keeps text legible). Static, so no per-frame
       cost even though it rides the streaming-text repaint. */
    text-shadow: 0 0 2px rgba(120, 210, 255, 0.55), 0 0 9px rgba(120, 210, 255, 0.42),
                 0 0 18px rgba(120, 210, 255, 0.22);
  }
  html[data-crt] body::before {
    content: ""; position: fixed; inset: 0; z-index: 9998; pointer-events: none;
    background:
      /* horizontal scanlines (4px period: 2px gap + 2px line) */
      repeating-linear-gradient(to bottom,
        rgba(0,0,0,0) 0, rgba(0,0,0,0) 2px,
        rgba(0,0,0,0.22) 2px, rgba(0,0,0,0.22) 4px),
      /* aperture grille — faint vertical RGB stripes (the aperture-grille tell, not a dot mask) */
      repeating-linear-gradient(to right,
        rgba(255,0,64,0.05) 0, rgba(0,255,128,0.05) 1px,
        rgba(64,128,255,0.05) 2px, rgba(0,0,0,0) 3px);
  }
  html[data-crt] body::after {
    content: ""; position: fixed; inset: 0; z-index: 9999; pointer-events: none;
    background:
      /* the two signature aperture-grille damper wires */
      linear-gradient(to bottom,
        transparent calc(33.3% - 1px), rgba(0,0,0,0.30) 33.3%, transparent calc(33.3% + 1px)),
      linear-gradient(to bottom,
        transparent calc(66.6% - 1px), rgba(0,0,0,0.30) 66.6%, transparent calc(66.6% + 1px)),
      /* tube-edge vignette */
      radial-gradient(ellipse 100% 100% at center, transparent 60%, rgba(0,0,0,0.55) 100%);
  }
  /* ---- Boxed messages (Appearance toggle, theme-agnostic) ---------------------------
     Each prompt/response on its own card surface: hard message boundaries and skimmable
     rhythm for long sessions, versus the default flat terminal look. Only theme variables,
     so every palette works. Excluded under W98 — its bevelled message windows are bespoke. */
  /* Frosted glass: cards are slightly translucent with a backdrop blur, so a chat
     background image glows through without ever fighting the text (the blur is what
     preserves contrast over busy wallpapers). Over a plain theme background the effect
     degrades to near-solid — no image, no cost to readability. Blur is static compositing,
     not per-frame work. */
  html[data-cards]:not([data-win98]) .user-echo {
    background: color-mix(in srgb, var(--panel) 82%, transparent);
    backdrop-filter: blur(6px);
    border: 1px solid var(--border); border-radius: 10px;
    padding: 8px 12px; }
  html[data-cards]:not([data-win98]) .assistant {
    background: color-mix(in srgb, var(--panel) 82%, transparent);
    backdrop-filter: blur(6px);
    border: 1px solid var(--border); border-radius: 10px;
    padding: 6px 12px 8px 12px; }
  /* Cards sit on the panel color, so code wells inside switch to the bg color to stay
     visually recessed (they normally use --panel against a --bg page). */
  html[data-cards]:not([data-win98]) .md pre,
  html[data-cards]:not([data-win98]) .md code { background: var(--bg); }

  /* ---- Windows 98 chrome -----------------------------------------------------------
     Scoped to html[data-win98]. The 3D language of 1998: silver surfaces, square corners,
     two-tone bevels lit from the top-left (raised = chrome you can press, sunken = wells
     that hold content), navy title-bar gradients, Tahoma, and none of the decoration the
     era didn't have (radii, soft shadows). Colors come from the theme's CSS variables;
     this block only reshapes geometry, bevels, and the title bars. All static — pairs
     with the theme's FlatMotion, because nothing animated in 1998. */
  html[data-win98] body { font-family: Tahoma, "MS Sans Serif", "Segoe UI", sans-serif;
    /* THE desktop teal. Silver never filled a screen in 1998 — it sat in windows on this. */
    background: #008080; padding: 12px 14px 20px 14px; }
  /* Each MESSAGE is its own window on the desktop (not one giant expanding one): user
     prompts are small silver windows; assistant responses are windows whose "MandoCode"
     label becomes the navy title bar — the hover copy/react chips land on it like window
     buttons. Status lines and tool ops sit directly on the teal like desktop icon labels,
     with brightened colors (the theme's dark semantic hues are unreadable on teal).
     (A user-chosen chat background image still paints over the teal via #bg — wallpaper.) */
  html[data-win98] .user-echo { background: var(--bg); padding: 7px 12px;
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF; }
  html[data-win98] .assistant { background: var(--bg);
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF; }
  html[data-win98] .assistant-label {
    background: linear-gradient(90deg, #000080, #1084D0); color: #FFFFFF;
    padding: 3px 10px; margin-bottom: 0; font-weight: 700; }
  html[data-win98] .assistant .md { padding: 2px 12px 8px 12px; }
  html[data-win98] .line { color: #EAF6F4; }
  html[data-win98] .line.info { color: #A8D8FF; }
  html[data-win98] .line.success { color: #90EE90; }
  html[data-win98] .line.warn { color: #FFE082; }
  html[data-win98] .line.error { color: #FF9E8F; }
  html[data-win98] .line.dim, html[data-win98] .op-meta, html[data-win98] .token-summary { color: #B8D8D4; }
  html[data-win98] .op { color: #EAF6F4; }
  html[data-win98] .op-path { color: #EAF6F4; }
  html[data-win98] .op-head a.file-link { color: #AAD4FF; border-bottom-color: #AAD4FF; }
  /* Op-head semantic colors (WebSearch/WebFetch/Write/Delete glyph classes) are theme-dark
     hues built for silver — brighten them on the teal, same mapping as the .line variants. */
  html[data-win98] .op-head.success { color: #90EE90; }
  html[data-win98] .op-head.error, html[data-win98] .op-head.red { color: #FF9E8F; }
  html[data-win98] .op-head.warn { color: #FFE082; }
  html[data-win98] .op-head.info, html[data-win98] .op-head.sky { color: #A8D8FF; }
  html[data-win98] .op-head.dim { color: #B8D8D4; }
  /* Square EVERYTHING. */
  html[data-win98] .panel, html[data-win98] .chip, html[data-win98] .tool-pill,
  html[data-win98] .copy-chip, html[data-win98] .react-ghost, html[data-win98] .expand-btn,
  html[data-win98] .web-toggle, html[data-win98] .dv-btn, html[data-win98] .ue-toggle,
  html[data-win98] .md pre, html[data-win98] .md code, html[data-win98] pre.mono-block,
  html[data-win98] pre.raw, html[data-win98] .op-detail, html[data-win98] #rx-pop,
  html[data-win98] .rx-pill, html[data-win98] #rx-pop .rx { border-radius: 0 !important; }
  /* Raised bevel: anything button-like is a silver 3D control. */
  html[data-win98] .copy-chip, html[data-win98] .react-ghost, html[data-win98] .expand-btn,
  html[data-win98] .web-toggle, html[data-win98] .dv-btn, html[data-win98] .ue-toggle,
  html[data-win98] .tool-pill, html[data-win98] .chip, html[data-win98] .rx-pill {
    background: var(--bg); color: #000;
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF;
  }
  /* ...and presses in like one. */
  html[data-win98] .copy-chip:active, html[data-win98] .expand-btn:active,
  html[data-win98] .web-toggle:active, html[data-win98] .dv-btn:active,
  html[data-win98] .ue-toggle:active, html[data-win98] .react-ghost:active {
    border-color: #404040 #FFFFFF #FFFFFF #404040;
  }
  /* Panels are little windows: raised silver frame + navy title-bar gradient. */
  html[data-win98] .panel {
    background: var(--bg);
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF;
  }
  html[data-win98] .panel-header {
    background: linear-gradient(90deg, #000080, #1084D0);
    color: #FFFFFF; border-bottom: none;
  }
  html[data-win98] .panel-header a.file-link { color: #FFFFFF; border-bottom-color: #9CC2E5; }
  /* Content wells are sunken white, like every 98 text box and list view. */
  html[data-win98] .md pre, html[data-win98] pre.cmd, html[data-win98] pre.cmd-out,
  html[data-win98] pre.diff, html[data-win98] pre.mono-block, html[data-win98] pre.raw,
  html[data-win98] .op-detail {
    background: var(--panel);
    border: 2px solid; border-color: #808080 #FFFFFF #FFFFFF #808080;
  }
  html[data-win98] .md code { background: var(--panel); border: 1px solid #808080; }
  html[data-win98] .md pre code { border: none; }
  /* 1998 had no soft shadows. */
  html[data-win98] #rx-pop { box-shadow: none; background: var(--bg);
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF; }
  html[data-win98] .chip .dot, html[data-win98] .tool-pill .tp-dot { box-shadow: none; }
  /* Plan/help tables become 98 list views: sunken white body, RAISED column headers —
     the iconic Explorer detail. Row separators in dialog-face gray. */
  html[data-win98] table.plan { background: var(--panel);
    border: 2px solid; border-color: #808080 #FFFFFF #FFFFFF #808080; }
  html[data-win98] table.plan th { background: var(--bg); color: #000;
    border: 1px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF; }
  html[data-win98] table.plan td { border-top: 1px solid #D4D0C8; }
  /* Chunky classic scrollbars. */
  html[data-win98] ::-webkit-scrollbar { width: 16px; height: 16px; }
  html[data-win98] ::-webkit-scrollbar-track { background: #DFDFDF; }
  html[data-win98] ::-webkit-scrollbar-thumb { background: var(--bg);
    border: 2px solid; border-color: #FFFFFF #404040 #404040 #FFFFFF; }
  html[data-win98] ::-webkit-scrollbar-corner { background: #DFDFDF; }

  /* User prompts: gold marks the user's voice, at normal weight so an 8-line clamped
     paste reads as text, not a block of emphasis. Only the sigil stays semibold. */
  .user-echo { color: var(--gold); white-space: pre-wrap; margin-top: 14px; }
  .ue-sigil { font-weight: 600; }
  /* Long prompts clamp to ~8 lines (JS adds the class only when the echo is actually tall).
     The fade is a mask on the text itself — not an overlay painted in a background color —
     so it works over chat-background images and every theme. */
  .user-echo.clamped { max-height: 11.5em; overflow: hidden;
    -webkit-mask-image: linear-gradient(to bottom, black calc(100% - 2.2em), transparent);
    mask-image: linear-gradient(to bottom, black calc(100% - 2.2em), transparent); }
  .ue-toggle { display: block; background: none; border: none; cursor: pointer;
    color: var(--dim); font-size: 11px; font-family: "Segoe UI", sans-serif; padding: 1px 0; }
  .ue-toggle:hover { color: var(--fg); }
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

  /* Collapsible long panels: a big write/diff/output otherwise fills the screen and forces
     endless scrolling, so panel-hosted blocks taller than ~22% of the window collapse to that
     preview height by default. A matching Expand/Collapse button sits in the top-RIGHT and
     bottom-RIGHT corners (JS adds them only when a block is actually tall) so it's reachable
     whether you're at the top or, after expanding, down at the bottom. The header and footer
     pad on the right to clear the buttons. Pure class flip on click — no animation loop. */
  .collapsible-panel { position: relative; }
  .collapsible-panel > .panel-header,
  .collapsible-panel > .panel-footer {
    padding-right: 84px;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  }
  /* Reserve a bottom gutter so the bottom corner buttons never overlap the last line of a
     footerless panel (e.g. command output). */
  pre.collapsible { position: relative; padding-bottom: 34px; }
  pre.collapsible.collapsed { max-height: 22vh; overflow-y: hidden; }
  .collapse-fade { position: absolute; left: 0; right: 0; bottom: 0; height: 44px;
    pointer-events: none; background: linear-gradient(to bottom, transparent, var(--panel)); }
  .expand-btn { position: absolute; top: 6px; z-index: 3; cursor: pointer;
    background: var(--bg); color: var(--dim); border: 1px solid var(--border);
    border-radius: 6px; padding: 2px 9px; font-size: 11px;
    font-family: "Segoe UI", sans-serif; opacity: 0.9; }
  .expand-btn.left { left: 6px; }
  .expand-btn.right { right: 6px; }
  .expand-btn.bottom { top: auto; bottom: 6px; }
  .expand-btn:hover { color: var(--fg); border-color: var(--accent); opacity: 1; }

  /* Web fetch/search previews: noisy reference text, hidden by default behind an inline Expand
     chip on the op line. Expanding reveals the detail box, which reuses the corner Collapse
     button (.expand-btn.right) so it can be closed from the window itself. */
  .web-toggle { margin-left: 8px; cursor: pointer; vertical-align: baseline;
    background: var(--bg); color: var(--dim); border: 1px solid var(--border);
    border-radius: 6px; padding: 1px 8px; font-size: 11px; font-family: "Segoe UI", sans-serif; }
  .web-toggle:hover { color: var(--fg); border-color: var(--accent); }
  .web-detail { position: relative; margin-top: 4px; }
  .web-detail[hidden] { display: none; }
  .web-detail > .op-detail { margin-top: 0; }
  /* Action chips on USER-requested diff cards (Changes-tab clicks): Undo posts to the host,
     Clear removes the card. Floated right in the header; the collapsible-panel header's
     right padding keeps them clear of the corner Expand button. */
  .dv-actions { float: right; display: inline-flex; gap: 6px; }
  .dv-btn { background: var(--bg); color: var(--dim); border: 1px solid var(--border);
    border-radius: 6px; padding: 1px 8px; font-size: 11px;
    font-family: "Segoe UI", sans-serif; cursor: pointer; }
  .dv-btn:hover { color: var(--fg); border-color: var(--accent); }
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
    // In the app, the host writes the clipboard (copy: message). In an EXPORTED transcript
    // there is no webview bridge, so fall back to the browser clipboard API — file:// pages
    // are a secure context in Chromium/Firefox, and this runs on a user gesture.
    if (window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage('copy:' + text);
    else if (navigator.clipboard)
      navigator.clipboard.writeText(text).catch(function () { });
    chip.classList.add('copied');
    setTimeout(function () { chip.classList.remove('copied'); }, 1400);
  }
  function addCopyChips() {
    log.querySelectorAll('.md pre:not([data-copy])').forEach(function (pre) {
      pre.setAttribute('data-copy', '1');
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      pre.appendChild(chip);      // click is handled by the delegated .copy-chip handler
    });
    log.querySelectorAll('.assistant:not([data-copy])').forEach(function (card) {
      card.setAttribute('data-copy', '1');
      if (!card.querySelector('.md')) return;
      const chip = document.createElement('button');
      chip.className = 'copy-chip';
      card.appendChild(chip);
    });
  }
  // Delegated so copy still works in exported transcripts (see the toggle handlers below).
  document.addEventListener('click', function (e) {
    const chip = e.target.closest('.copy-chip');
    if (!chip) return;
    e.stopPropagation();
    const pre = chip.closest('pre');
    let text = '';
    if (pre) {
      const code = pre.querySelector('code');
      text = code ? code.innerText : pre.innerText;
    } else {
      const card = chip.closest('.assistant');
      const md = card && card.querySelector('.md');
      if (md) text = md.innerText;
    }
    doCopy(text, chip);
  });

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

  // --- collapse long diff/output panels to a preview; corner buttons maximize/minimize ---
  // Only panel-hosted <pre> blocks (diffs, command output, folder-delete listings) taller than
  // ~22% of the window get collapsed. A matching Expand/Collapse button is placed in the top-RIGHT
  // and bottom-RIGHT corners so it's reachable from the top or — after expanding down — the bottom.
  function setCollapsed(pre, collapsed) {
    pre.classList.toggle('collapsed', collapsed);
    const panel = pre.closest('.panel');
    if (!panel) return;
    const fade = panel.querySelector('.collapse-fade');
    if (fade) fade.style.display = collapsed ? 'block' : 'none';
    panel.querySelectorAll('.expand-btn').forEach(function (b) {
      b.textContent = collapsed ? '⤢ Expand' : '⤡ Collapse';
    });
  }
  function addCollapsers() {
    log.querySelectorAll('pre.diff:not([data-collapse]), pre.cmd-out:not([data-collapse])').forEach(function (pre) {
      pre.setAttribute('data-collapse', '1');
      const panel = pre.closest('.panel');
      if (!panel) return;                                               // only panel-hosted blocks
      if (pre.scrollHeight <= window.innerHeight * 0.22 + 40) return;   // short enough already
      pre.classList.add('collapsible');
      panel.classList.add('collapsible-panel');
      const fade = document.createElement('div');
      fade.className = 'collapse-fade';
      pre.appendChild(fade);
      ['right', 'right bottom'].forEach(function (side) {
        const b = document.createElement('button');
        b.className = 'expand-btn ' + side;
        b.title = 'Maximize / minimize this block';
        panel.appendChild(b);   // click is handled by the delegated .expand-btn handler
      });
      setCollapsed(pre, true);                                          // start minimized
    });
  }

  // --- clamp long user prompts: a big pasted prompt (log, file contents) otherwise
  // dominates the scrollback, so echoes taller than ~9 lines clamp to ~8 with a toggle ---
  function addEchoClamps() {
    log.querySelectorAll('.user-echo:not([data-clamp])').forEach(function (echo) {
      echo.setAttribute('data-clamp', '1');
      const lh = parseFloat(getComputedStyle(echo).lineHeight) || 20;
      if (echo.scrollHeight <= lh * 9 + 6) return;   // short enough — no chrome
      // Count hidden lines while the echo is still unclamped; ~8 lines stay visible.
      const hidden = Math.max(1, Math.round(echo.scrollHeight / lh) - 8);
      echo.classList.add('clamped');
      const btn = document.createElement('button');
      btn.className = 'ue-toggle';
      // The expanded label lives in a data attribute (not a closure) so it survives
      // outerHTML serialization when the transcript is exported.
      btn.dataset.more = 'Show more (' + hidden + ' more line' + (hidden === 1 ? '' : 's') + ')';
      btn.textContent = btn.dataset.more;
      echo.after(btn);
    });
  }
  // Toggles are DELEGATED document handlers, not per-button listeners: exporting the
  // transcript serializes outerHTML, which keeps the buttons but drops bound listeners.
  // These handlers re-register when the exported page runs this script on load, so
  // clamped prompts and collapsed panels stay expandable in the saved file.
  document.addEventListener('click', function (e) {
    const btn = e.target.closest('.ue-toggle');
    if (!btn) return;
    const echo = btn.previousElementSibling;
    if (!echo || !echo.classList.contains('user-echo')) return;
    const clamped = echo.classList.toggle('clamped');
    btn.textContent = clamped ? btn.dataset.more : 'Show less';
  });
  document.addEventListener('click', function (e) {
    const b = e.target.closest('.expand-btn:not(.web-collapse)');
    if (!b) return;
    const panel = b.closest('.panel');
    const pre = panel && panel.querySelector('pre.collapsible');
    if (pre) setCollapsed(pre, !pre.classList.contains('collapsed'));
  });

  window.__append = function (html) {
    const nearBottom = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 60);
    const wrap = document.createElement('div');
    wrap.innerHTML = html;
    while (wrap.firstChild) placeChild(wrap.firstChild);
    highlightNew();
    addCopyChips();
    addReactionGhosts();
    addCollapsers();
    addEchoClamps();
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

  // Interactive diff-card chips (delegated — survives transcript export, like the toggles).
  // Clear just deletes the card from the DOM; Undo asks the host, which confirms before
  // discarding anything. In an exported page Undo is a harmless no-op (no webview bridge).
  document.addEventListener('click', function (e) {
    const clear = e.target.closest('.dv-clear');
    if (clear) {
      const panel = clear.closest('.panel');
      if (panel) panel.remove();
      return;
    }
    const undo = e.target.closest('.dv-undo');
    if (undo && window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage('undo-file:' + undo.getAttribute('data-file'));
  });

  // --- drag hand-off: Chromium owns drags over the transcript surface, so XAML never sees
  // them. On dragenter we alert the host, which mounts its drop overlay over this WebView;
  // the OS then retargets the drag (and the drop, with real file paths) to that overlay.
  // preventDefault on dragover/drop is the safety net for a drop that lands in the instant
  // before the overlay mounts — without it the browser would navigate to the dropped file.
  window.addEventListener('dragenter', function (e) {
    e.preventDefault();
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage('drag-enter');
  });
  window.addEventListener('dragover', function (e) { e.preventDefault(); });
  window.addEventListener('drop', function (e) { e.preventDefault(); });

  // Web fetch/search preview toggle: the inline chip opens the hidden detail box; the box's own
  // Collapse button (and the chip again) closes it. Chip label and box visibility stay in sync.
  document.addEventListener('click', function (e) {
    const t = e.target.closest('.web-toggle, .web-collapse');
    if (!t) return;
    e.stopPropagation();
    const op = t.closest('.op');
    if (!op) return;
    const detail = op.querySelector('.web-detail');
    const toggle = op.querySelector('.web-toggle');
    if (!detail || !toggle) return;
    const open = t.classList.contains('web-collapse') ? false : detail.hasAttribute('hidden');
    detail.toggleAttribute('hidden', !open);
    toggle.textContent = open ? '⤡ Collapse' : '⤢ Expand';
  });

  // --- jump-to-bottom pill ---
  const pill = document.createElement('div');
  pill.id = 'jump-pill';
  pill.textContent = '↓ Latest';
  document.body.appendChild(pill);
  pill.addEventListener('click', function () {
    window.scrollTo({ top: document.body.scrollHeight,
      behavior: document.documentElement.hasAttribute('data-flat') ? 'auto' : 'smooth' });
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
