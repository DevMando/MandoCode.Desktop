namespace MandoCode.Desktop;

/// <summary>Immutable row for command, file, agent, and emoji suggestions.</summary>
public sealed class CommandSuggestion
{
    public string Command { get; init; } = "";
    public string Description { get; init; } = "";
    public string? InsertText { get; init; }
}
