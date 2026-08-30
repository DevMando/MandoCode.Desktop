using MandoCode.Models;

namespace MandoCode.Desktop.ViewModels;

/// <summary>Applies user review edits to the instruction that a plan step will execute.</summary>
public static class PlanInstructionEditor
{
    public static bool Apply(TaskStep step, string? revisedInstruction)
    {
        if (string.IsNullOrWhiteSpace(revisedInstruction))
            return false;

        var revised = revisedInstruction.Trim();
        step.Instruction = revised;
        // The description is a UI label, but a stale label makes the review card misleading.
        // Always derive it from the instruction the user actually approved.
        step.Description = revised.Length > 60 ? revised[..57] + "..." : revised;

        return true;
    }
}
