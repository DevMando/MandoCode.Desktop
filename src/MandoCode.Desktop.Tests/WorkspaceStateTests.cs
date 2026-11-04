using System.Text.Json;
using MandoCode.Desktop.Services;
using Xunit;

namespace MandoCode.Desktop.Tests;

public class WorkspaceStateTests
{
    [Fact]
    public void LegacyShapeBecomesMainWithoutLosingSessionOrSplitKeys()
    {
        var shape = new WorkspaceShape(new() { new("Atlas", "C:/project", "model", "a"),
            new("Nova", "C:/other", "other-model", "b") }, 1, new() { "a", "b" }, new() { .3, .7 }, new() { 1 });
        var restored = WorkspaceState.Parse(JsonSerializer.Serialize(shape));
        var main = Assert.Single(restored.Workspaces);
        Assert.Equal("Main", main.Name);
        Assert.Equal(main.Id, restored.ActiveWorkspaceId);
        Assert.Equal(JsonSerializer.Serialize(shape), JsonSerializer.Serialize(main.Shape));
    }

    [Fact]
    public void MultipleWorkspacesRoundTripIncludingEmptyWorkspaceAndIndependentLayouts()
    {
        var state = new DesktopWorkspaceState(1, "second", new() {
            new("first", "Website", new(new() { new("Atlas", "C:/site", "model", "a") }, 0)),
            new("second", "Backend", new(new(), 0)) });
        var json = JsonSerializer.Serialize(state);
        Assert.Equal(json, JsonSerializer.Serialize(WorkspaceState.Parse(json)));
    }

    [Fact]
    public void DuplicateWorkspaceIdsAreRejected()
    {
        var state = new DesktopWorkspaceState(1, "same", new() {
            new("same", "One", new(new(), 0)), new("same", "Two", new(new(), 0)) });
        Assert.Throws<InvalidDataException>(() => WorkspaceState.Parse(JsonSerializer.Serialize(state)));
    }
}
