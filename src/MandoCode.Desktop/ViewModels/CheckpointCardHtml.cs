using System.Net;
using MandoCode.Services;

namespace MandoCode.Desktop.ViewModels;

/// <summary>Builds the native, actionable checkpoint card shown in the Desktop transcript.</summary>
public static class CheckpointCardHtml
{
    public static string Build(PlanRunState saved)
    {
        var outstanding = PlanCheckpointStore.OutstandingSteps(saved);
        var done = saved.Steps.Count - outstanding;
        var goal = OneLine(saved.Goal);
        return "<div class=\"panel checkpoint-card\">" +
               "<div class=\"panel-header sky\">Unfinished plan</div>" +
               $"<div class=\"checkpoint-body\"><div class=\"checkpoint-goal\">{E(goal)}</div>" +
               $"<div class=\"checkpoint-progress\">{done} of {saved.Steps.Count} steps settled · {outstanding} remaining</div></div>" +
               "<div class=\"checkpoint-actions\">" +
               "<button class=\"checkpoint-btn checkpoint-resume\">Resume</button>" +
               "<button class=\"checkpoint-btn checkpoint-discard\">Discard</button>" +
               "<span class=\"checkpoint-state\"></span></div></div>";
    }

    private static string OneLine(string value)
    {
        var text = string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return text.Length <= 180 ? text : text[..177] + "...";
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
