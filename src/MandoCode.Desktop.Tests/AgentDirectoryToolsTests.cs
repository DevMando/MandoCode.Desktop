using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

/// <summary>
/// What the model actually receives when it asks about another agent. These are read tools, so the
/// bar is that a wrong or missing name produces something the model can act on rather than a dead
/// end — a failed lookup should tell it who IS open.
/// </summary>
public class AgentDirectoryToolsTests
{
    private const string Self = "self-key";

    private static AgentEntry Agent(string key, string name, bool busy = false, bool cmd = false,
                                    int step = 0, int total = 0) =>
        new(key, name, $@"C:\src\{name}", name.ToLowerInvariant(), "qwen3:8b", busy, cmd, step, total);

    private static (AgentDirectoryTools Tools, AgentDirectory Dir) Setup(params AgentEntry[] agents)
    {
        var d = new AgentDirectory();
        d.Replace(agents.Append(Agent(Self, "Sonic")));
        return (new AgentDirectoryTools(d, Self), d);
    }

    [Fact]
    public void StatusReportsWorkingWithPlanPosition()
    {
        var (tools, _) = Setup(Agent("k1", "Ninja", busy: true, cmd: true, step: 5, total: 6));

        var status = tools.GetAgentStatus("Ninja");

        Assert.Contains("WORKING", status);
        Assert.Contains("step 5 of 6", status);
        Assert.Contains("shell command is running", status);
    }

    [Fact]
    public void StatusAnswersTheQuestionEvenWhileTheAgentIsMidTurn()
    {
        // The whole point: "have you finished X?" is asked precisely when the answer might be no.
        // This tool reads state, so a busy agent is an answer rather than a refusal.
        var (tools, _) = Setup(Agent("k1", "Ninja", busy: true));

        var status = tools.GetAgentStatus("Ninja");

        Assert.DoesNotContain("busy", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WORKING", status);
    }

    [Fact]
    public void AnUnknownNameNamesTheAgentsThatDoExist()
    {
        // A bare "not found" leaves the model guessing. Listing the real names lets it recover in
        // the same turn, which is usually a typo or a half-remembered callsign.
        var (tools, _) = Setup(Agent("k1", "Ninja"), Agent("k2", "Falchion"));

        var status = tools.GetAgentStatus("Knuckles");

        Assert.Contains("No agent named \"Knuckles\"", status);
        Assert.Contains("Ninja", status);
        Assert.Contains("Falchion", status);
    }

    [Fact]
    public void AskingAboutYourselfSaysSoRatherThanReportingNothing()
    {
        var (tools, _) = Setup();

        var status = tools.GetAgentStatus("Sonic");

        Assert.Contains("this agent", status);
    }

    [Fact]
    public void ListingExcludesTheAskerAndMarksWhoIsWorking()
    {
        var (tools, _) = Setup(Agent("k1", "Ninja", busy: true), Agent("k2", "Falchion"));

        var list = tools.ListAgents();

        Assert.DoesNotContain("Sonic", list);
        Assert.Contains("Ninja", list);
        Assert.Contains("(working)", list);
        Assert.Contains("(idle)", list);
    }

    [Fact]
    public void ASoleAgentIsToldItIsAlone()
    {
        // Distinct from "not found": there is nobody to mention, so the model should stop looking
        // rather than try another spelling.
        var (tools, _) = Setup();

        Assert.Contains("No other agents are open", tools.ListAgents());
        Assert.Contains("no other agents open at all", tools.GetAgentStatus("Ninja"));
    }
}
