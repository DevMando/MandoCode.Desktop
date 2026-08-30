using System.Net;
using System.Text;
using MandoCode.Models;

namespace MandoCode.Desktop.Services;

/// <summary>Builds the reviewable, escaped transcript card for a proposed plan.</summary>
public static class PlanCardHtml
{
    public static string Build(TaskPlan plan)
    {
        static string Escape(string? text) => WebUtility.HtmlEncode(text ?? "");

        var sb = new StringBuilder();
        sb.Append("<div class=\"panel\"><div class=\"panel-header sky\">Proposed plan</div><table class=\"plan\">");
        sb.Append("<tr><th>Step</th><th>Description</th><th>What it will do</th></tr>");
        foreach (var step in plan.Steps)
            sb.Append($"<tr><td class=\"sky\">{step.StepNumber}</td><td>{Escape(step.Description)}</td>" +
                      $"<td class=\"dim\">{Escape(step.Instruction)}</td></tr>");
        sb.Append("</table></div>");
        return sb.ToString();
    }
}
