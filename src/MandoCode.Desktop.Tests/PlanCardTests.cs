using MandoCode.Desktop.Services;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class PlanCardTests
{
    [Fact]
    public void PlanCard_ShowsExecutableInstructionsAndEscapesThem()
    {
        var plan = new TaskPlan
        {
            Steps =
            [
                new TaskStep
                {
                    StepNumber = 1,
                    Description = "Update the client",
                    Instruction = "Edit <ApiClient.cs> & run focused tests."
                }
            ]
        };

        var html = PlanCardHtml.Build(plan);

        Assert.Contains("What it will do", html);
        Assert.Contains("Edit &lt;ApiClient.cs&gt; &amp; run focused tests.", html);
        Assert.DoesNotContain("Edit <ApiClient.cs>", html);
    }
}
