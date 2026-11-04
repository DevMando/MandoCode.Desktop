using System.Net;
using System.Text.RegularExpressions;

namespace MandoCode.Desktop.Services;

/// <summary>Old model setup notices describe a prior runtime, not the restored conversation.</summary>
public static class ModelNoticeReplay
{
    /// <summary>
    /// The cloud-subscription notice a model switch used to emit. No longer produced — the model
    /// chip already marks a model as cloud, and ResponseStreamer says the actionable version if a
    /// 403 actually arrives — but it sits in the journals of anyone who switched to a cloud model
    /// before it was removed, so replay must still recognise it. Both wordings are listed: the
    /// second shipped in 0.14.x and the first replaced it, and a journal can hold either.
    /// </summary>
    private static readonly string[] RetiredCloudNotices =
    {
        "Cloud models run on ollama.com and need an active cloud subscription.",
        "Cloud model — runs on ollama.com's servers and needs an account with an active cloud " +
        "subscription. Without one, requests return 403 Forbidden.",
    };

    public static bool IsTransient(string html)
    {
        // Match only standalone system notices. Keep user messages, assistant explanations,
        // and errors even when they quote the same wording.
        var match = Regex.Match(html,
            "\\A<div class=\"notice-card dim\"><span class=\"notice-emoji\">[^<]*</span><span class=\"notice-text\">([^<]*)</span></div>\\z");
        if (!match.Success) return false;

        // Decoded, because the journal holds the ESCAPED text: the retired wording contains an
        // apostrophe, which is written as &#39; and would never match a raw C# literal.
        var text = WebUtility.HtmlDecode(match.Groups[1].Value);

        return RetiredCloudNotices.Contains(text)
            || Regex.IsMatch(text, @"\AContext window sized to \d+k tokens for this model tier \(applies from your next message\)\.\z");
    }
}
