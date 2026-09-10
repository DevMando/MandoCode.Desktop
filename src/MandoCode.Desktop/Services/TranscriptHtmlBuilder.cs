using System.Net;
using System.Text;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Desktop.ViewModels;
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

    /// <summary>Step results are part of the live-plan surface, so they retain a solid readable
    /// panel even when ordinary transcript messages are configured as flat.</summary>
    public string PlanStepResult(string markdown, string? speaker = null) =>
        $"<div class=\"assistant plan-step-result\"><div class=\"assistant-label\">{E(speaker ?? "MandoCode")}</div><div class=\"md\">{FromMarkdown(markdown)}</div></div>";

    // System notices need their own surface. A theme can intentionally place a high-opacity image
    // behind a flat transcript, so color alone is not enough contrast for actions, warnings, or
    // recovery guidance. Keep ordinary user/assistant messages untouched; only these notices use
    // the compact card treatment.
    public string Info(string text) => NoticeCard(text, "info");
    public string Success(string text) => NoticeCard(text, "success");
    public string Warn(string text) => NoticeCard(text, "warn");
    public string Error(string text) => NoticeCard(text, "error");
    public string Dim(string text) => NoticeCard(text, "dim");

    /// <summary>Low-priority operational output. Unlike notices, this is folded into the turn's
    /// expandable Activity section once the work finishes.</summary>
    public string Activity(string text, string state = "") => NoticeCard(text, state, " activity-item");

    /// <summary>A completed approval decision. It keeps the existing card while live, then joins
    /// the compact, distinct-emoji approval summary when the turn finishes.</summary>
    public string ApprovalNotice(string text, string state = "success") =>
        NoticeCard(text, state, " approval-activity-item", ActivityEmoji(state));

    /// <summary>
    /// A high-contrast, live plan-status card. Plan execution and step headers must remain readable
    /// even when a user has turned their chat wallpaper opacity up, so they get a themed surface
    /// rather than being painted directly on the transcript background.
    /// </summary>
    public string PlanStarted(int totalSteps) =>
        PlanActivity("Executing plan", $"Preparing {totalSteps} step{(totalSteps == 1 ? "" : "s")}");

    /// <summary>The "switching folders" progress line. Lives here as a const because
    /// <see cref="IsEphemeralStatus"/> matches on it verbatim — reworded in one place only, the
    /// notice would silently start replaying on session restore.</summary>
    public const string ProjectSwitchNotice = "Switching to the new project — this conversation is kept…";

    /// <summary>True for blocks that describe LIVE session state (status chips: connection,
    /// model ready, MCP counts, pending offers) rather than conversation history. Session
    /// restore replays journaled transcripts — replaying a dead process's state pills next
    /// to the new session's real ones ("MCP connected" twice, stale "ready") reads as
    /// duplicate/conflicting status, so replay skips them. Journals still CONTAIN them:
    /// capture stays dumb and faithful; the judgment lives at replay.</summary>
    public static bool IsEphemeralStatus(string blockHtml) =>
        blockHtml.StartsWith("<div class=\"chip-row\"", StringComparison.Ordinal)
        || blockHtml.StartsWith("<div class=\"panel checkpoint-card\"", StringComparison.Ordinal)
        // Boot/progress narration — true only while it was happening. ("Project root
        // changed to: X" is deliberately NOT here: that's a real event, kept as history.)
        || blockHtml.Contains($">{ProjectSwitchNotice}<", StringComparison.Ordinal)
        || blockHtml.Contains(">✓ Ready.<", StringComparison.Ordinal)
        || ModelNoticeReplay.IsTransient(blockHtml);

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

    private static string NoticeCard(string text, string state, string extraClass = "", string? activityIcon = null) =>
        $"<div class=\"notice-card {state}{extraClass}\"{ActivityIconAttribute(activityIcon)}><span class=\"notice-emoji\">{NoticeEmoji(state)}</span>" +
        $"<span class=\"notice-text\">{E(text)}</span></div>";

    private static string NoticeEmoji(string state) => state switch
    {
        "success" => "✅",
        "warn" => "⚠️",
        "error" => "❌",
        _ => "ℹ️",
    };

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
        $"<div class=\"tool-pill activity-item\"><span class=\"tp-dot\"></span>" +
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
        $"<div class=\"panel\" data-work-kind=\"command\"><div class=\"panel-header sky\">Command</div><pre class=\"cmd\">$ {E(command)}</pre></div>";

    public string CommandOutputCard(string command, string output, bool failed = false) =>
        $"<div class=\"panel\"{(failed ? "" : " data-work-kind=\"command-output\"")}><div class=\"panel-header {(failed ? "red" : "sky")}\">$ {E(command)}</div><pre class=\"cmd-out\">{E(output)}</pre></div>";

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
        sb.Append($"<div class=\"panel\" data-work-kind=\"diff\"><div class=\"panel-header sky\">Diff: {FileLink(relativePath)}{actions}</div><pre class=\"diff\">");
        AppendDiffLines(sb, lines);
        sb.Append("</pre>");
        sb.Append($"<div class=\"panel-footer\">{E(summary)}</div></div>");
        return sb.ToString();
    }

    public string FolderDeleteCard(string relativePath, string listing) =>
        $"<div class=\"panel red-border\" data-work-kind=\"folder-delete\"><div class=\"panel-header red\">Delete Folder: {FileLink(relativePath)}/</div><pre class=\"cmd-out\">{E(listing)}</pre></div>";

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
        sb.Append("<div class=\"op activity-item\">");
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

    public string PlanCard(TaskPlan plan) => PlanCardHtml.Build(plan);

    public string CheckpointCard(PlanRunState saved) => CheckpointCardHtml.Build(saved);

    public string StepStarted(int current, int total, string description) =>
        PlanActivity($"Step {current}/{total}", description);

    public string StepCompleted(int current, int total) =>
        PlanActivity($"Step {current}/{total} complete", "Moving to the next step", "success");

    public string StepFailed(int current, int total, string message) =>
        PlanActivity($"Step {current}/{total} needs attention", message, "error");

    public string PlanFinished(string title, string detail, string state) =>
        PlanActivity(title, detail, state);

    /// <summary>A high-contrast, themed status surface for important system notices.</summary>
    public string StatusCard(string title, string detail, string state = "") =>
        PlanActivity(title, detail, state);

    public string ApprovalActivity(string title, string detail, string state) =>
        PlanActivity(title, detail, state, " approval-activity-item", ActivityEmoji(state));

    private static string PlanActivity(
        string title,
        string detail,
        string state = "",
        string extraClass = "",
        string? activityIcon = null) =>
        $"<div class=\"plan-activity {state}{extraClass}\"{ActivityIconAttribute(activityIcon)}><span class=\"plan-activity-dot\"></span>" +
        $"<div><div class=\"plan-activity-title\"><span class=\"plan-activity-emoji\">{ActivityEmoji(state)}</span>{E(title)}</div>" +
        $"<div class=\"plan-activity-detail\">{E(detail)}</div></div></div>";

    private static string ActivityIconAttribute(string? icon) =>
        icon == null ? "" : $" data-activity-icon=\"{E(icon)}\"";

    private static string ActivityEmoji(string state) => state switch
    {
        "success" => "✅",
        "warning" => "⚠️",
        "error" => "❌",
        _ => "▶️",
    };

    /// <summary>
    /// The per-turn token metric, as a pill rather than a trailing line of faint text.
    ///
    /// <para>Note the wrapper is NOT <c>chip-row</c>, even though the pill inside is an ordinary
    /// <c>chip</c>: <see cref="IsEphemeralStatus"/> drops every <c>chip-row</c> from a restored
    /// session, on the grounds that status pills describe a dead process's live state. A token
    /// count is the opposite — it is what that turn actually cost, and it belongs to the
    /// conversation's history. Reusing the row class here would have silently deleted these from
    /// every reopened session.</para>
    /// </summary>
    public string TokenSummary(string text) =>
        $"<div class=\"token-summary\"><span class=\"chip token-chip\">{E(text)}</span></div>";

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
<html{{(theme.FlatMotion ? " data-flat=\"1\"" : "")}}{{(theme.Crt ? " data-crt=\"1\"" : "")}}{{(theme.Win98 ? " data-win98=\"1\"" : "")}}{{(theme.Monochrome ? " data-mono=\"1\"" : "")}}{{(theme.Fanfold ? " data-fanfold=\"1\"" : "")}}{{(theme.Lcd ? " data-lcd=\"1\"" : "")}}{{(theme.Riso ? " data-riso=\"1\"" : "")}}{{(theme.Vfd ? " data-vfd=\"1\"" : "")}}{{(theme.SplitFlap ? " data-splitflap=\"1\"" : "")}}{{(theme.Fiche ? " data-fiche=\"1\"" : "")}}{{(theme.Cyanotype ? " data-cyano=\"1\"" : "")}}{{(theme.Vector ? " data-vector=\"1\"" : "")}}{{(ThemeManager.BoxedMessages ? " data-cards=\"1\"" : "")}}{{(ThemeManager.MediaBackground ? " data-mediabg=\"1\"" : "")}}>
<head>
<meta charset="utf-8">
<script src="https://mandocode.assets/highlight.min.js"></script>
<style>
  :root {
    --bg: {{theme.Background}};
    --fg: {{theme.Text}};
    --dim: {{theme.ReadableDim}};
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

  <!-- Matrix LCD. A passive-matrix panel cannot show a photograph: it has a handful of addressable
       levels and one colour of backlight. So the picture is quantised to FOUR shades (the dither
       is what stops that banding into posterised mush) and then mapped onto the panel's own olive
       ramp — darkest is the polarizer at full twist, lightest is the backlight through bare glass.
       The result is the Game Boy Camera, which is exactly what a photo on this hardware looked
       like. Same structure as #eink, one extra level and a duotone tail. -->
  <filter id="lcd" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="g"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="g" result="gc">
      <feFuncR type="linear" slope="1.25" intercept="-0.12"/>
      <feFuncG type="linear" slope="1.25" intercept="-0.12"/>
      <feFuncB type="linear" slope="1.25" intercept="-0.12"/>
    </feComponentTransfer>
    <feImage result="ltile" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="ltile" result="lbayer"/>
    <feComposite in="gc" in2="lbayer" operator="arithmetic" k1="0" k2="1" k3="-0.34" k4="0.17" result="ld"/>
    <feComponentTransfer in="ld" result="lq">
      <feFuncR type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncG type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncB type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
    <!-- #1B2410 (polarizer, darkest) through #C4CFA1 (backlight, lightest) -->
    <feComponentTransfer in="lq">
      <feFuncR type="table" tableValues="0.106 0.769"/>
      <feFuncG type="table" tableValues="0.141 0.812"/>
      <feFuncB type="table" tableValues="0.063 0.631"/>
    </feComponentTransfer>
  </filter>

  <!-- Pheteven Phosphor. One gun, one phosphor — the tube physically cannot resolve a colour image,
       so the picture becomes an amber duotone. Deliberately NOT dithered, unlike the paper and
       panel filters: a CRT draws continuous tone, and banding it would be borrowing a limitation
       from the wrong medium. Contrast is pushed because the scanline overlay lands on top. -->
  <filter id="amber" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="ag"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="ag" result="agc">
      <feFuncR type="linear" slope="1.30" intercept="-0.16"/>
      <feFuncG type="linear" slope="1.30" intercept="-0.16"/>
      <feFuncB type="linear" slope="1.30" intercept="-0.16"/>
    </feComponentTransfer>
    <!-- #100A02 (unlit tube) through #FFB000 (P3 amber at full beam) -->
    <feComponentTransfer in="agc">
      <feFuncR type="table" tableValues="0.063 1.000"/>
      <feFuncG type="table" tableValues="0.039 0.690"/>
      <feFuncB type="table" tableValues="0.008 0.000"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>

  <!-- Green-Bar Fanfold. A line printer renders an image the only way it can: one ribbon, struck
       or not struck, at whatever density the dot pitch allows. So this is 1-bit like #eink — but
       mapped to ribbon ink on warm stock rather than black on white, because the paper is not
       white and the ink is not black. -->
  <filter id="fanfold" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="fg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="fg" result="fgc">
      <feFuncR type="linear" slope="1.40" intercept="-0.20"/>
      <feFuncG type="linear" slope="1.40" intercept="-0.20"/>
      <feFuncB type="linear" slope="1.40" intercept="-0.20"/>
    </feComponentTransfer>
    <feImage result="ftile" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="ftile" result="fbayer"/>
    <feComposite in="fgc" in2="fbayer" operator="arithmetic" k1="0" k2="1" k3="-1" k4="0.5" result="fd"/>
    <feComponentTransfer in="fd" result="fq">
      <feFuncR type="discrete" tableValues="0 1"/>
      <feFuncG type="discrete" tableValues="0 1"/>
      <feFuncB type="discrete" tableValues="0 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
    <!-- #2A2822 (ribbon) through #F4F1E4 (stock) -->
    <feComponentTransfer in="fq">
      <feFuncR type="table" tableValues="0.165 0.957"/>
      <feFuncG type="table" tableValues="0.157 0.945"/>
      <feFuncB type="table" tableValues="0.133 0.894"/>
    </feComponentTransfer>
  </filter>

  <!-- Risograph. A riso reproduces a photo as halftone in its loaded inks — so the picture is
       dithered like the paper filters, then mapped across BOTH drums: blue in the shadows, pink
       through the midtones, bare stock in the highlights. Three stops rather than two is what
       makes it read as a two-colour print instead of a blue-tinted photograph. Six levels, because
       a stencil holds more tone than a printer ribbon does. -->
  <filter id="riso" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="rg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="rg" result="rgc">
      <feFuncR type="linear" slope="1.20" intercept="-0.10"/>
      <feFuncG type="linear" slope="1.20" intercept="-0.10"/>
      <feFuncB type="linear" slope="1.20" intercept="-0.10"/>
    </feComponentTransfer>
    <feImage result="rtile" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="rtile" result="rbayer"/>
    <feComposite in="rgc" in2="rbayer" operator="arithmetic" k1="0" k2="1" k3="-0.2" k4="0.1" result="rd"/>
    <feComponentTransfer in="rd" result="rq">
      <feFuncR type="discrete" tableValues="0 0.2 0.4 0.6 0.8 1"/>
      <feFuncG type="discrete" tableValues="0 0.2 0.4 0.6 0.8 1"/>
      <feFuncB type="discrete" tableValues="0 0.2 0.4 0.6 0.8 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
    <!-- #1A4A8F (federal blue) → #C42A63 (fluorescent pink) → #F3EFE6 (stock) -->
    <feComponentTransfer in="rq">
      <feFuncR type="table" tableValues="0.102 0.769 0.953"/>
      <feFuncG type="table" tableValues="0.290 0.165 0.937"/>
      <feFuncB type="table" tableValues="0.561 0.388 0.902"/>
    </feComponentTransfer>
  </filter>

  <!-- VFD. The panel has one phosphor colour, so a picture behind it becomes a cyan duotone. Not dithered — a VFD segment is fully on or fully off, but the image is behind the SMOKED GLASS rather than in the segments, so it keeps continuous tone and simply loses its colour. -->
  <filter id="vfd" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="vfdg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="vfdg" result="vfdc">
      <feFuncR type="linear" slope="1.3" intercept="-0.16"/>
      <feFuncG type="linear" slope="1.3" intercept="-0.16"/>
      <feFuncB type="linear" slope="1.3" intercept="-0.16"/>
    </feComponentTransfer>
    <feComponentTransfer in="vfdc">
      <feFuncR type="table" tableValues="0.020 0.498"/>
      <feFuncG type="table" tableValues="0.031 1.000"/>
      <feFuncB type="table" tableValues="0.039 0.894"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>

  <!-- Split-Flap. A flap is a printed card: it holds ink or it doesn't, so the picture is 1-bit and lands as the board's own two colours. Coarser dithering than the paper filters, because a board's resolution is measured in whole characters. -->
  <filter id="splitflap" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="splitflapg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="splitflapg" result="splitflapc">
      <feFuncR type="linear" slope="1.45" intercept="-0.22"/>
      <feFuncG type="linear" slope="1.45" intercept="-0.22"/>
      <feFuncB type="linear" slope="1.45" intercept="-0.22"/>
    </feComponentTransfer>
    <feImage result="splitflapt" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="splitflapt" result="splitflapb"/>
    <feComposite in="splitflapc" in2="splitflapb" operator="arithmetic" k1="0" k2="1" k3="-1" k4="0.5" result="splitflapd"/>
    <feComponentTransfer in="splitflapd" result="splitflapq">
      <feFuncR type="discrete" tableValues="0 1"/>
      <feFuncG type="discrete" tableValues="0 1"/>
      <feFuncB type="discrete" tableValues="0 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
    <feComponentTransfer in="splitflapq">
      <feFuncR type="table" tableValues="0.063 0.961"/>
      <feFuncG type="table" tableValues="0.063 0.918"/>
      <feFuncB type="table" tableValues="0.078 0.784"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>

  <!-- Microfiche. Silver-halide film in a cool reader — continuous tone, no colour, and a slightly compressed range because a projected positive never reaches true black. -->
  <filter id="fiche" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="ficheg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="ficheg" result="fichec">
      <feFuncR type="linear" slope="1.05" intercept="0.02"/>
      <feFuncG type="linear" slope="1.05" intercept="0.02"/>
      <feFuncB type="linear" slope="1.05" intercept="0.02"/>
    </feComponentTransfer>
    <feComponentTransfer in="fichec">
      <feFuncR type="table" tableValues="0.227 0.851"/>
      <feFuncG type="table" tableValues="0.275 0.875"/>
      <feFuncB type="table" tableValues="0.314 0.886"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>

  <!-- Cyanotype. A photogram of the picture: exposure runs from unexposed paper to saturated Prussian blue. Inverted against the others, because on a cyanotype the LIGHT areas of a negative are what turn blue. -->
  <filter id="cyano" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="cyanog"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="cyanog" result="cyanoc">
      <feFuncR type="linear" slope="1.15" intercept="-0.06"/>
      <feFuncG type="linear" slope="1.15" intercept="-0.06"/>
      <feFuncB type="linear" slope="1.15" intercept="-0.06"/>
    </feComponentTransfer>
    <feComponentTransfer in="cyanoc">
      <feFuncR type="table" tableValues="0.929 0.055"/>
      <feFuncG type="table" tableValues="0.957 0.227"/>
      <feFuncB type="table" tableValues="0.973 0.373"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
  </filter>

  <!-- Vector Scope. A scope cannot display a raster image at all — a beam draws strokes. So the picture is reduced to a few phosphor intensities, which is as close as steered-beam hardware gets to a photograph. -->
  <filter id="vector" x="0%" y="0%" width="100%" height="100%" color-interpolation-filters="sRGB">
    <feColorMatrix type="matrix" result="vectorg"
      values="0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0.299 0.587 0.114 0 0  0 0 0 1 0"/>
    <feComponentTransfer in="vectorg" result="vectorc">
      <feFuncR type="linear" slope="1.25" intercept="-0.14"/>
      <feFuncG type="linear" slope="1.25" intercept="-0.14"/>
      <feFuncB type="linear" slope="1.25" intercept="-0.14"/>
    </feComponentTransfer>
    <feImage result="vectort" x="0" y="0" width="16" height="16" preserveAspectRatio="none"
      href="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAo0lEQVR42pXLEXMCABgA0C4IgiAIBkEQBINgEAyCIAiCYBAEgyAYDILugiAIgiAIgiAIgmAwGARBMBgEwWAQBIPBYHe7e//ge/4SSYYUWJJmTIk1iXB4o8onHQ7UudAlHspsyTKlwgs3zImHb554p8kvfU48EA8ZJtyyIcWIIivi4UiDKz321PjikXi455U8C+7YkWNGPPwx4EybH575oEU4/AOvd36QFSHM3wAAAABJRU5ErkJggg=="/>
    <feTile in="vectort" result="vectorb"/>
    <feComposite in="vectorc" in2="vectorb" operator="arithmetic" k1="0" k2="1" k3="-0.34" k4="0.17" result="vectord"/>
    <feComponentTransfer in="vectord" result="vectorq">
      <feFuncR type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncG type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncB type="discrete" tableValues="0 0.34 0.67 1"/>
      <feFuncA type="discrete" tableValues="1 1"/>
    </feComponentTransfer>
    <feComponentTransfer in="vectorq">
      <feFuncR type="table" tableValues="0.024 0.475"/>
      <feFuncG type="table" tableValues="0.059 0.851"/>
      <feFuncB type="table" tableValues="0.035 0.573"/>
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
