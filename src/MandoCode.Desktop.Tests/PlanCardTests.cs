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

    [Fact]
    public void PlanCard_HidesHostBrowserContextFromTheReviewer()
    {
        var context = BrowserRequestContext.Capture("924730e0888f4721a78eecfe166bd402", "https://example.com/search");
        var plan = new TaskPlan
        {
            Steps =
            [
                new TaskStep
                {
                    StepNumber = 1,
                    Description = "Read the results",
                    Instruction = BrowserRequestContext.Attach("Inspect the page and summarize it.", context)
                }
            ]
        };

        var html = PlanCardHtml.Build(plan);

        Assert.Contains("Inspect the page and summarize it.", html);
        Assert.DoesNotContain("924730e0888f4721a78eecfe166bd402", html);
        Assert.DoesNotContain("Host browser context", html);
        Assert.DoesNotContain("untrusted data", html);
    }
}
