using System.Text.RegularExpressions;

namespace MandoCode.Desktop.Services;

/// <summary>Old model setup notices describe a prior runtime, not the restored conversation.</summary>
public static class ModelNoticeReplay
{
    public static bool IsTransient(string html)
    {
        // Match only standalone system notices. Keep user messages, assistant explanations,
        // and errors even when they quote the same wording.
        var match = Regex.Match(html,
            "\\A<div class=\"notice-card dim\"><span class=\"notice-emoji\">[^<]*</span><span class=\"notice-text\">([^<]*)</span></div>\\z");
        if (!match.Success) return false;
        var text = match.Groups[1].Value;
        return text == "Cloud models run on ollama.com and need an active cloud subscription."
            || Regex.IsMatch(text, @"\AContext window sized to \d+k tokens for this model tier \(applies from your next message\)\.\z");
    }
}
