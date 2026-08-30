using MandoCode.Desktop.ViewModels;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class CheckpointCardHtmlTests
{
    [Fact]
    public void Build_ShowsProgressActionsAndEscapesGoal()
    {
        var state = new PlanRunState
        {
            Goal = "Fix <planner> & tests",
            Steps =
            [
                new PlanStepState { Number = 1, Description = "done", Status = TaskStepStatus.Completed },
                new PlanStepState { Number = 2, Description = "left", Status = TaskStepStatus.Pending }
            ]
        };

        var html = CheckpointCardHtml.Build(state);

        Assert.Contains("Fix &lt;planner&gt; &amp; tests", html);
        Assert.Contains("1 of 2 steps settled", html);
        Assert.Contains("checkpoint-resume", html);
        Assert.Contains("checkpoint-discard", html);
    }
}
