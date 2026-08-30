using MandoCode.Desktop.ViewModels;
using MandoCode.Models;
using Xunit;

namespace MandoCode.Desktop.Tests;

public sealed class PlanInstructionEditorTests
{
    [Fact]
    public void Apply_ReplacesExecutableInstruction_AndRefreshesShortLabel()
    {
        var step = new TaskStep
        {
            Description = "Edit the API",
            Instruction = "Change every API file."
        };

        var changed = PlanInstructionEditor.Apply(step, "  Change only ApiClient.cs and run its tests.  ");

        Assert.True(changed);
        Assert.Equal("Change only ApiClient.cs and run its tests.", step.Instruction);
        Assert.Equal(step.Instruction, step.Description);
    }

    [Fact]
    public void Apply_BlankInstruction_LeavesStepUnchanged()
    {
        var step = new TaskStep { Description = "Keep me", Instruction = "Keep this instruction." };

        var changed = PlanInstructionEditor.Apply(step, "   ");

        Assert.False(changed);
        Assert.Equal("Keep this instruction.", step.Instruction);
        Assert.Equal("Keep me", step.Description);
    }

    [Fact]
    public void Apply_LongInstruction_ReplacesStaleLabelWithBoundedCurrentLabel()
    {
        var step = new TaskStep { Description = "Update authentication", Instruction = "Old." };
        var longInstruction = new string('x', 80);

        PlanInstructionEditor.Apply(step, longInstruction);

        Assert.Equal(longInstruction, step.Instruction);
        Assert.Equal(60, step.Description.Length);
        Assert.EndsWith("...", step.Description);
    }

    [Fact]
    public void Apply_LongInstruction_CreatesBoundedLabelWhenMissing()
    {
        var step = new TaskStep { Description = "", Instruction = "Old." };

        PlanInstructionEditor.Apply(step, new string('x', 80));

        Assert.Equal(60, step.Description.Length);
        Assert.EndsWith("...", step.Description);
    }
}
